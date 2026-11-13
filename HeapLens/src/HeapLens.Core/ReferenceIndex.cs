using Microsoft.Diagnostics.Runtime;

namespace HeapLens.Core;

/// <summary>
/// 引用索引（对标 dotMemory 的 References / 调用链视图）：
/// 一次堆遍历建立"对象 ← 引用者"反向索引与 GC 根表，之后任意对象的
/// 保留路径（GC 根 → … → 对象 的完整调用链）均可即时计算，
/// 无需逐对象运行耗时的 GCRoot 全堆搜索。
///
/// 内存布局（内存优化）：反向引用边存于扁平数组，按"子对象地址 → 入边链"
/// 链式前向星组织（head/next），字段名驻留（intern）去重——避免旧实现
/// "每对象一个 List"带来的百万级小对象分配与数倍内存开销。
/// </summary>
public sealed class ReferenceIndex
{
    /// <summary>直接引用者信息（调用链上一层）。</summary>
    public sealed record ReferrerInfo(ulong Parent, string TypeName, long Size, string? Field, bool IsArrayElement);

    /// <summary>直接被引用对象信息（调用链下一层）。</summary>
    public sealed record ChildInfo(ulong Address, string TypeName, long Size, string? Field, bool IsArrayElement);

    /// <summary>GC 根分组（按根类型）。</summary>
    public sealed record RootGroupInfo(string Kind, int TotalCount, IReadOnlyList<ulong> Addresses);

    /// <summary>对象简要信息。</summary>
    public sealed record NodeBrief(string TypeName, long Size);

    private readonly ClrHeap _heap;

    // ---- 反向引用边：扁平数组 + 链式前向星（子对象地址 → 入边链） ----
    private readonly Dictionary<ulong, int> _head = new();
    private ulong[] _edgeParent = new ulong[1 << 12];
    private int[] _edgeField = new int[1 << 12]; // 字段名驻留下标（FieldNull / FieldArrayElement 为特殊值）
    private int[] _edgeNext = new int[1 << 12];  // 同一子对象的下一条入边（-1 结束）
    private int _edgeCount;

    // ---- 字段名驻留表（同名字段只存一份字符串） ----
    private readonly List<string> _fieldNames = new();
    private readonly Dictionary<string, int> _fieldIndexOf = new(StringComparer.Ordinal);
    private const int FieldNull = -1;
    private const int FieldArrayElement = -2;

    private readonly Dictionary<ulong, string> _roots = new();
    private readonly Dictionary<string, List<(ulong Address, long Size)>> _byType = new(StringComparer.Ordinal);

    private ReferenceIndex(ClrHeap heap) => _heap = heap;

    public long ObjectCount { get; private set; }
    public long EdgeCount { get; private set; }
    public int RootCount => _roots.Count;

    /// <summary>全部 GC 根对象地址。供支配树等扩展分析使用。</summary>
    public IReadOnlyCollection<ulong> RootAddresses => _roots.Keys;

    /// <summary>枚举全部反向引用边（子对象, 引用者）。供支配树建图使用。</summary>
    public IEnumerable<(ulong Child, ulong Parent)> EnumerateReferrerEdges()
    {
        foreach ((ulong child, int head) in _head)
            for (int e = head; e != -1; e = _edgeNext[e])
                yield return (child, _edgeParent[e]);
    }

    // ---------- 构建 ----------

    /// <summary>一次堆遍历构建索引。大堆几十秒量级，建议后台线程调用并订阅 progress。</summary>
    public static ReferenceIndex Build(ClrHeap heap, Action<long>? progress = null, CancellationToken ct = default)
    {
        var idx = new ReferenceIndex(heap);
        long n = 0;
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            ct.ThrowIfCancellationRequested();
            n++;
            if (!obj.IsFree)
            {
                string name = GetTypeName(obj);
                if (!idx._byType.TryGetValue(name, out List<(ulong, long)>? list))
                    idx._byType[name] = list = new List<(ulong, long)>();
                list.Add((obj.Address, (long)obj.Size));

                foreach (ClrReference r in obj.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: true))
                {
                    ulong child = r.Object.Address;
                    if (child == 0) continue;
                    idx.AddEdge(child, obj.Address, idx.InternField(r.Field?.Name, r.IsArrayElement));
                }
            }
            if (n % 200_000 == 0) progress?.Invoke(n);
        }
        idx.ObjectCount = n;
        idx.EdgeCount = idx._edgeCount;
        idx.TrimEdges();

        foreach (IClrRoot root in heap.EnumerateRoots())
        {
            ulong a = root.Object.Address;
            if (a != 0 && !idx._roots.ContainsKey(a))
                idx._roots[a] = root.RootKind.ToString();
        }
        return idx;
    }

    private int InternField(string? field, bool isArrayElement)
    {
        if (isArrayElement) return FieldArrayElement;
        if (field == null) return FieldNull;
        if (_fieldIndexOf.TryGetValue(field, out int idx)) return idx;
        idx = _fieldNames.Count;
        _fieldNames.Add(field);
        _fieldIndexOf[field] = idx;
        return idx;
    }

    private string? FieldNameOf(int fieldIdx) => fieldIdx switch
    {
        FieldArrayElement => "[数组元素]",
        FieldNull => null,
        _ => _fieldNames[fieldIdx],
    };

    private void AddEdge(ulong child, ulong parent, int fieldIdx)
    {
        if (_edgeCount == _edgeNext.Length)
        {
            int cap = _edgeNext.Length * 2;
            Array.Resize(ref _edgeParent, cap);
            Array.Resize(ref _edgeField, cap);
            Array.Resize(ref _edgeNext, cap);
        }
        int i = _edgeCount++;
        _edgeParent[i] = parent;
        _edgeField[i] = fieldIdx;
        _edgeNext[i] = _head.TryGetValue(child, out int h) ? h : -1;
        _head[child] = i;
    }

    /// <summary>构建完成后裁掉边数组尾部空闲容量。</summary>
    private void TrimEdges()
    {
        if (_edgeCount == _edgeNext.Length) return;
        Array.Resize(ref _edgeParent, _edgeCount);
        Array.Resize(ref _edgeField, _edgeCount);
        Array.Resize(ref _edgeNext, _edgeCount);
    }

    // ---------- 调用链（保留路径） ----------

    /// <summary>
    /// 目标对象的调用链（GC 根 → … → 对象）。返回按深度升序的至多 maxPaths 条
    /// 不同路径：首条为最短路径（BFS），其余为备选路径（DFS 枚举，带状态上限）。
    /// </summary>
    public IReadOnlyList<RetentionPath> GetRetentionPaths(ulong target, int maxPaths = 3)
    {
        var paths = new List<RetentionPath>();
        var sigs = new HashSet<string>(StringComparer.Ordinal);

        List<(ulong Parent, int FieldIdx)>? shortest = BfsShortestChain(target);
        if (shortest != null)
        {
            RetentionPath p = BuildPath(target, shortest);
            paths.Add(p);
            sigs.Add(Signature(p));
        }

        foreach (List<(ulong Parent, int FieldIdx)> chain in DfsChains(target, maxPaths * 3))
        {
            RetentionPath p = BuildPath(target, chain);
            if (sigs.Add(Signature(p))) paths.Add(p);
            if (paths.Count >= maxPaths) break;
        }

        paths.Sort((a, b) => a.Nodes.Count.CompareTo(b.Nodes.Count));
        return paths.Count > maxPaths ? paths.GetRange(0, maxPaths) : paths;
    }

    /// <summary>BFS 求最短保留链。返回"根侧在前"的边列表；未找到返回 null。</summary>
    private List<(ulong Parent, int FieldIdx)>? BfsShortestChain(ulong target)
    {
        if (_roots.ContainsKey(target)) return new List<(ulong, int)>();

        var came = new Dictionary<ulong, (ulong Child, ulong Parent, int FieldIdx)>();
        var visited = new HashSet<ulong> { target };
        var queue = new Queue<ulong>();
        queue.Enqueue(target);
        ulong? found = null;
        const int maxStates = 300_000;
        int states = 0;

        while (queue.Count > 0 && states < maxStates)
        {
            ulong cur = queue.Dequeue();
            states++;
            if (_roots.ContainsKey(cur)) { found = cur; break; }
            if (!_head.TryGetValue(cur, out int e)) continue;
            for (; e != -1; e = _edgeNext[e])
            {
                ulong parent = _edgeParent[e];
                if (visited.Add(parent))
                {
                    came[parent] = (cur, parent, _edgeField[e]);
                    queue.Enqueue(parent);
                }
            }
        }
        if (found == null) return null;

        var chain = new List<(ulong, int)>();
        ulong node = found.Value;
        while (node != target)
        {
            (ulong child, ulong parent, int fieldIdx) = came[node];
            chain.Add((parent, fieldIdx)); // 根侧在前
            node = child;
        }
        return chain;
    }

    /// <summary>DFS 枚举备选保留链（目标向上，带深度/状态上限），"根侧在前"返回。</summary>
    private IEnumerable<List<(ulong Parent, int FieldIdx)>> DfsChains(ulong target, int maxResults, int maxDepth = 64, int maxStates = 200_000)
    {
        var results = new List<List<(ulong, int)>>();
        int states = 0;
        var onPath = new HashSet<ulong> { target };
        var stack = new List<(ulong, int)>(); // 目标侧在前

        void Dfs(ulong cur)
        {
            if (results.Count >= maxResults || states > maxStates || stack.Count >= maxDepth) return;
            if (cur != target && _roots.ContainsKey(cur))
            {
                var chain = new List<(ulong, int)>(stack);
                chain.Reverse(); // 根侧在前
                results.Add(chain);
                return;
            }
            if (!_head.TryGetValue(cur, out int e)) return;
            for (; e != -1; e = _edgeNext[e])
            {
                states++;
                ulong parent = _edgeParent[e];
                if (!onPath.Add(parent)) continue;
                stack.Add((parent, _edgeField[e]));
                Dfs(parent);
                stack.RemoveAt(stack.Count - 1);
                onPath.Remove(parent);
                if (results.Count >= maxResults || states > maxStates) return;
            }
        }

        Dfs(target);
        return results;
    }

    /// <summary>把"根侧在前"的边链组装为 RetentionPath（节点根 → 目标）。</summary>
    private RetentionPath BuildPath(ulong target, List<(ulong Parent, int FieldIdx)> chain)
    {
        int k = chain.Count;
        var path = new RetentionPath
        {
            RootKind = k > 0 && _roots.TryGetValue(chain[0].Parent, out string? kind) ? kind : (k == 0 ? "自身即根" : "—"),
        };
        for (int i = 0; i < k; i++)
            path.Nodes.Add(MakeNode(chain[i].Parent, i > 0 ? FieldNameOf(chain[i - 1].FieldIdx) : null));
        path.Nodes.Add(MakeNode(target, k > 0 ? FieldNameOf(chain[k - 1].FieldIdx) : null));
        return path;
    }

    private static string Signature(RetentionPath p)
        => string.Join(">", p.Nodes.Select(n => n.Address.ToString("X")));

    private RetentionNode MakeNode(ulong addr, string? field)
    {
        ClrObject obj = _heap.GetObject(addr);
        bool ok = obj.IsValid;
        return new RetentionNode
        {
            Address = addr,
            TypeName = ok ? GetTypeName(obj) : "<unknown>",
            FieldName = field,
            Size = ok ? (long)obj.Size : 0,
            Preview = ok ? TryPreview(obj) : null,
        };
    }

    // ---------- 树浏览（GC 根 → 下级引用） ----------

    /// <summary>GC 根按根类型分组（每组截断至 perGroupCap 个地址）。</summary>
    public IReadOnlyList<RootGroupInfo> GetRootGroups(int perGroupCap = 200)
    {
        var groups = new Dictionary<string, List<ulong>>(StringComparer.Ordinal);
        foreach ((ulong addr, string kind) in _roots)
        {
            if (!groups.TryGetValue(kind, out List<ulong>? list))
                groups[kind] = list = new List<ulong>();
            list.Add(addr);
        }
        var result = new List<RootGroupInfo>();
        foreach ((string kind, List<ulong> addrs) in groups)
        {
            addrs.Sort();
            int total = addrs.Count;
            result.Add(new RootGroupInfo(kind, total, addrs.Count > perGroupCap ? addrs.GetRange(0, perGroupCap) : addrs));
        }
        result.Sort((a, b) => b.TotalCount.CompareTo(a.TotalCount));
        return result;
    }

    /// <summary>对象的直接被引用对象（调用链下一层），用于树展开。</summary>
    public IReadOnlyList<ChildInfo> GetChildren(ulong addr, int cap = 300)
    {
        ClrObject obj = _heap.GetObject(addr);
        if (!obj.IsValid) return Array.Empty<ChildInfo>();
        var list = new List<ChildInfo>();
        foreach (ClrReference r in obj.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: false))
        {
            if (r.Object.Address == 0 || r.Object.IsFree) continue;
            list.Add(new ChildInfo(r.Object.Address, GetTypeName(r.Object), (long)r.Object.Size,
                r.IsArrayElement ? "[数组元素]" : r.Field?.Name, r.IsArrayElement));
            if (list.Count > cap) break;
        }
        return list;
    }

    /// <summary>对象的全部直接引用者（调用链上一层的所有候选）。</summary>
    public IReadOnlyList<ReferrerInfo> GetReferrerInfos(ulong addr)
    {
        if (!_head.TryGetValue(addr, out int e)) return Array.Empty<ReferrerInfo>();
        var list = new List<ReferrerInfo>();
        for (; e != -1; e = _edgeNext[e])
        {
            ulong parent = _edgeParent[e];
            ClrObject obj = _heap.GetObject(parent);
            bool ok = obj.IsValid;
            list.Add(new ReferrerInfo(parent, ok ? GetTypeName(obj) : "<unknown>", ok ? (long)obj.Size : 0,
                FieldNameOf(_edgeField[e]), _edgeField[e] == FieldArrayElement));
        }
        list.Sort((a, b) => b.Size.CompareTo(a.Size));
        return list;
    }

    // ---------- 实例 / 查询 ----------

    /// <summary>按类型取实例（构建期已建索引，瞬时返回，按大小降序）。</summary>
    public IReadOnlyList<InstanceInfo> GetInstances(string typeName, int limit)
    {
        if (!_byType.TryGetValue(typeName, out List<(ulong Address, long Size)>? list))
            return Array.Empty<InstanceInfo>();
        var sorted = new List<(ulong, long)>(list);
        sorted.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        int take = Math.Min(limit, sorted.Count);
        var result = new List<InstanceInfo>(take);
        for (int i = 0; i < take; i++)
        {
            ClrObject obj = _heap.GetObject(sorted[i].Item1);
            result.Add(new InstanceInfo
            {
                Address = sorted[i].Item1,
                Size = sorted[i].Item2,
                Preview = obj.IsValid ? TryPreview(obj) : null,
            });
        }
        return result;
    }

    /// <summary>对象简要信息（类型名 + 大小）。</summary>
    public NodeBrief GetBrief(ulong addr)
    {
        ClrObject obj = _heap.GetObject(addr);
        return obj.IsValid ? new NodeBrief(GetTypeName(obj), (long)obj.Size) : new NodeBrief("<unknown>", 0);
    }

    public bool IsRoot(ulong addr) => _roots.ContainsKey(addr);
    public string? GetRootKind(ulong addr) => _roots.TryGetValue(addr, out string? k) ? k : null;

    // ---------- 合并持有链（对标 dotMemory merged retention paths） ----------

    /// <summary>构建期临时节点：按地址去重聚合对象数/大小/保留大小。</summary>
    private sealed class MBNode
    {
        public string TypeName = "";
        public string? Field;
        public readonly HashSet<ulong> TargetAddrs = new();
        public readonly Dictionary<ulong, long> AddrSizes = new();
        public readonly Dictionary<ulong, long> AddrRetained = new();
        public readonly List<MBNode> Children = new();
        public MBNode? FindChild(string type, string? field)
            => Children.FirstOrDefault(c => c.TypeName == type && c.Field == field);
    }

    /// <summary>合并持有链树结果：树 + 覆盖统计。</summary>
    public sealed record MergedTreeResult(MergedChainNode Root, int TotalInstances, int LinkedInstances, int TotalPaths);

    /// <summary>
    /// 某类型所有实例的持有链合并树（GC 根 → 目标类型）：
    /// 取该类型前 maxInstances 个实例（按大小降序）各至多 maxPathsPerInstance 条保留路径，
    /// 按 (持有者类型, 字段) 逐层合并聚合，节点显示经此持有的目标实例数 / 去重对象数 / 大小合计 / 保留大小合计。
    /// retainedByAddr 传入支配树 Retained 映射（DominatorAnalysis.RetainedByAddr）时，节点聚合 Retained。
    /// </summary>
    public MergedTreeResult GetMergedRetentionTree(string typeName, int maxInstances = 200, int maxPathsPerInstance = 2,
        IReadOnlyDictionary<ulong, long>? retainedByAddr = null)
    {
        var root = new MBNode { TypeName = "GC 根" };
        IReadOnlyList<InstanceInfo> insts = GetInstances(typeName, maxInstances);
        int linked = 0, totalPaths = 0;
        foreach (InstanceInfo inst in insts)
        {
            IReadOnlyList<RetentionPath> paths = GetRetentionPaths(inst.Address, maxPathsPerInstance);
            if (paths.Count > 0) linked++;
            totalPaths += paths.Count;
            foreach (RetentionPath path in paths)
                MergePath(root, path, inst.Address, retainedByAddr);
        }
        AggregateTargets(root);
        return new MergedTreeResult(Materialize(root), insts.Count, linked, totalPaths);
    }

    /// <summary>把一条根→目标的保留路径合并进持有链树（按类型名+字段聚合，地址去重）。</summary>
    private static void MergePath(MBNode root, RetentionPath path, ulong targetAddr, IReadOnlyDictionary<ulong, long>? retainedByAddr)
    {
        if (path.Nodes.Count == 0) return;
        MBNode cur = root;
        for (int i = 0; i < path.Nodes.Count; i++)
        {
            RetentionNode n = path.Nodes[i];
            // BuildPath 赋予节点的 FieldName 语义是"被父节点经此字段持有"（入边字段），
            // 因此本节点的合并键字段应取 path.Nodes[i].FieldName（修复原取 [i-1] 的错位 bug）。
            string? field = i == 0 ? null : n.FieldName;
            MBNode? child = cur.FindChild(n.TypeName, field);
            if (child == null)
            {
                child = new MBNode { TypeName = n.TypeName, Field = field };
                cur.Children.Add(child);
            }
            child.AddrSizes[n.Address] = n.Size;
            child.AddrRetained[n.Address] = retainedByAddr != null && retainedByAddr.TryGetValue(n.Address, out long r) ? r : n.Size;
            cur = child;
        }
        cur.TargetAddrs.Add(targetAddr); // 叶子 = 目标实例
    }

    /// <summary>后序把子树目标实例并入本节点（TargetCount = 经此节点到达的目标实例数）。</summary>
    private static void AggregateTargets(MBNode n)
    {
        foreach (MBNode c in n.Children)
        {
            AggregateTargets(c);
            n.TargetAddrs.UnionWith(c.TargetAddrs);
        }
    }

    private static MergedChainNode Materialize(MBNode n)
    {
        var node = new MergedChainNode
        {
            TypeName = n.TypeName,
            Field = n.Field,
            TargetCount = n.TargetAddrs.Count,
            ObjectCount = n.AddrSizes.Count,
            TotalSize = n.AddrSizes.Values.Sum(),
            RetainedSize = n.AddrRetained.Values.Sum(),
        };
        foreach (MBNode c in n.Children) node.Children.Add(Materialize(c));
        node.Children.Sort((a, b) => b.TargetCount.CompareTo(a.TargetCount));
        return node;
    }

    // ---------- 内部 ----------

    private static string GetTypeName(ClrObject obj)
    {
        try { return obj.Type?.Name ?? "<unknown>"; }
        catch { return "<unknown>"; }
    }

    private static string? TryPreview(ClrObject obj)
    {
        try
        {
            if (obj.Type != null && obj.Type.Name == "System.String")
            {
                string s = obj.AsString(120) ?? "";
                return s.Length > 120 ? s[..120] + "…" : s;
            }
        }
        catch { }
        return null;
    }
}
