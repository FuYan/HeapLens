using Microsoft.Diagnostics.Runtime;

namespace HeapLens.Core;

/// <summary>
/// 引用索引（对标 dotMemory 的 References / 调用链视图）：
/// 一次堆遍历建立"对象 ← 引用者"反向索引与 GC 根表，之后任意对象的
/// 保留路径（GC 根 → … → 对象 的完整调用链）均可即时计算，
/// 无需逐对象运行耗时的 GCRoot 全堆搜索。
/// </summary>
public sealed class ReferenceIndex
{
    /// <summary>一条反向引用边：Parent 通过 Field 引用了本对象。</summary>
    public readonly record struct ReferrerEdge(ulong Parent, string? Field, bool IsArrayElement);

    /// <summary>直接引用者信息（调用链上一层）。</summary>
    public sealed record ReferrerInfo(ulong Parent, string TypeName, long Size, string? Field, bool IsArrayElement);

    /// <summary>直接被引用对象信息（调用链下一层）。</summary>
    public sealed record ChildInfo(ulong Address, string TypeName, long Size, string? Field, bool IsArrayElement);

    /// <summary>GC 根分组（按根类型）。</summary>
    public sealed record RootGroupInfo(string Kind, int TotalCount, IReadOnlyList<ulong> Addresses);

    /// <summary>对象简要信息。</summary>
    public sealed record NodeBrief(string TypeName, long Size);

    private readonly ClrHeap _heap;
    private readonly Dictionary<ulong, List<ReferrerEdge>> _referrers = new();
    private readonly Dictionary<ulong, string> _roots = new();
    private readonly Dictionary<string, List<(ulong Address, long Size)>> _byType = new(StringComparer.Ordinal);

    private ReferenceIndex(ClrHeap heap) => _heap = heap;

    public long ObjectCount { get; private set; }
    public long EdgeCount { get; private set; }
    public int RootCount => _roots.Count;

    /// <summary>反向引用表（子对象地址 → 引用者边列表）。供支配树等扩展分析使用。</summary>
    public IReadOnlyDictionary<ulong, List<ReferrerEdge>> Referrers => _referrers;

    /// <summary>全部 GC 根对象地址。供支配树等扩展分析使用。</summary>
    public IReadOnlyCollection<ulong> RootAddresses => _roots.Keys;

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
                    if (!idx._referrers.TryGetValue(child, out List<ReferrerEdge>? edges))
                        idx._referrers[child] = edges = new List<ReferrerEdge>();
                    edges.Add(new ReferrerEdge(obj.Address, r.IsArrayElement ? "[数组元素]" : r.Field?.Name, r.IsArrayElement));
                    idx.EdgeCount++;
                }
            }
            if (n % 200_000 == 0) progress?.Invoke(n);
        }
        idx.ObjectCount = n;

        foreach (IClrRoot root in heap.EnumerateRoots())
        {
            ulong a = root.Object.Address;
            if (a != 0 && !idx._roots.ContainsKey(a))
                idx._roots[a] = root.RootKind.ToString();
        }
        return idx;
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

        List<ReferrerEdge>? shortest = BfsShortestChain(target);
        if (shortest != null)
        {
            RetentionPath p = BuildPath(target, shortest);
            paths.Add(p);
            sigs.Add(Signature(p));
        }

        foreach (List<ReferrerEdge> chain in DfsChains(target, maxPaths * 3))
        {
            RetentionPath p = BuildPath(target, chain);
            if (sigs.Add(Signature(p))) paths.Add(p);
            if (paths.Count >= maxPaths) break;
        }

        paths.Sort((a, b) => a.Nodes.Count.CompareTo(b.Nodes.Count));
        return paths.Count > maxPaths ? paths.GetRange(0, maxPaths) : paths;
    }

    /// <summary>BFS 求最短保留链。返回"根侧在前"的边列表；未找到返回 null。</summary>
    private List<ReferrerEdge>? BfsShortestChain(ulong target)
    {
        if (_roots.ContainsKey(target)) return new List<ReferrerEdge>();

        var came = new Dictionary<ulong, (ulong Child, ReferrerEdge Edge)>();
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
            if (!_referrers.TryGetValue(cur, out List<ReferrerEdge>? edges)) continue;
            foreach (ReferrerEdge e in edges)
            {
                if (visited.Add(e.Parent))
                {
                    came[e.Parent] = (cur, e);
                    queue.Enqueue(e.Parent);
                }
            }
        }
        if (found == null) return null;

        var chain = new List<ReferrerEdge>();
        ulong node = found.Value;
        while (node != target)
        {
            (ulong child, ReferrerEdge edge) = came[node];
            chain.Add(edge); // 根侧在前
            node = child;
        }
        return chain;
    }

    /// <summary>DFS 枚举备选保留链（目标向上，带深度/状态上限），"根侧在前"返回。</summary>
    private IEnumerable<List<ReferrerEdge>> DfsChains(ulong target, int maxResults, int maxDepth = 64, int maxStates = 200_000)
    {
        var results = new List<List<ReferrerEdge>>();
        int states = 0;
        var onPath = new HashSet<ulong> { target };
        var stack = new List<ReferrerEdge>(); // 目标侧在前

        void Dfs(ulong cur)
        {
            if (results.Count >= maxResults || states > maxStates || stack.Count >= maxDepth) return;
            if (cur != target && _roots.ContainsKey(cur))
            {
                var chain = new List<ReferrerEdge>(stack);
                chain.Reverse(); // 根侧在前
                results.Add(chain);
                return;
            }
            if (!_referrers.TryGetValue(cur, out List<ReferrerEdge>? edges)) return;
            foreach (ReferrerEdge e in edges)
            {
                states++;
                if (!onPath.Add(e.Parent)) continue;
                stack.Add(e);
                Dfs(e.Parent);
                stack.RemoveAt(stack.Count - 1);
                onPath.Remove(e.Parent);
                if (results.Count >= maxResults || states > maxStates) return;
            }
        }

        Dfs(target);
        return results;
    }

    /// <summary>把"根侧在前"的边链组装为 RetentionPath（节点根 → 目标）。</summary>
    private RetentionPath BuildPath(ulong target, List<ReferrerEdge> chain)
    {
        int k = chain.Count;
        var path = new RetentionPath
        {
            RootKind = k > 0 && _roots.TryGetValue(chain[0].Parent, out string? kind) ? kind : (k == 0 ? "自身即根" : "—"),
        };
        for (int i = 0; i < k; i++)
            path.Nodes.Add(MakeNode(chain[i].Parent, i > 0 ? chain[i - 1].Field : null));
        path.Nodes.Add(MakeNode(target, k > 0 ? chain[k - 1].Field : null));
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
        if (!_referrers.TryGetValue(addr, out List<ReferrerEdge>? edges)) return Array.Empty<ReferrerInfo>();
        var list = new List<ReferrerInfo>(edges.Count);
        foreach (ReferrerEdge e in edges)
        {
            ClrObject obj = _heap.GetObject(e.Parent);
            bool ok = obj.IsValid;
            list.Add(new ReferrerInfo(e.Parent, ok ? GetTypeName(obj) : "<unknown>", ok ? (long)obj.Size : 0, e.Field, e.IsArrayElement));
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

    /// <summary>构建期临时节点：按地址去重聚合对象数/大小。</summary>
    private sealed class MBNode
    {
        public string TypeName = "";
        public string? Field;
        public readonly HashSet<ulong> TargetAddrs = new();
        public readonly Dictionary<ulong, long> AddrSizes = new();
        public readonly List<MBNode> Children = new();
        public MBNode? FindChild(string type, string? field)
            => Children.FirstOrDefault(c => c.TypeName == type && c.Field == field);
    }

    /// <summary>合并持有链树结果：树 + 覆盖统计。</summary>
    public sealed record MergedTreeResult(MergedChainNode Root, int TotalInstances, int LinkedInstances, int TotalPaths);

    /// <summary>
    /// 某类型所有实例的持有链合并树（GC 根 → 目标类型）：
    /// 取该类型前 maxInstances 个实例（按大小降序）各至多 maxPathsPerInstance 条保留路径，
    /// 按 (持有者类型, 字段) 逐层合并聚合，节点显示经此持有的目标实例数 / 去重对象数 / 大小合计。
    /// </summary>
    public MergedTreeResult GetMergedRetentionTree(string typeName, int maxInstances = 200, int maxPathsPerInstance = 2)
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
                MergePath(root, path, inst.Address);
        }
        AggregateTargets(root);
        return new MergedTreeResult(Materialize(root), insts.Count, linked, totalPaths);
    }

    /// <summary>把一条根→目标的保留路径合并进持有链树（按类型名+字段聚合，地址去重）。</summary>
    private static void MergePath(MBNode root, RetentionPath path, ulong targetAddr)
    {
        if (path.Nodes.Count == 0) return;
        MBNode cur = root;
        for (int i = 0; i < path.Nodes.Count; i++)
        {
            RetentionNode n = path.Nodes[i];
            string? field = i == 0 ? null : path.Nodes[i - 1].FieldName; // 父节点经由该字段持有本节点
            MBNode? child = cur.FindChild(n.TypeName, field);
            if (child == null)
            {
                child = new MBNode { TypeName = n.TypeName, Field = field };
                cur.Children.Add(child);
            }
            child.AddrSizes[n.Address] = n.Size;
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
