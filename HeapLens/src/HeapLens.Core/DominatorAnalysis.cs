namespace HeapLens.Core;

/// <summary>
/// 支配树分析：计算每个对象的 Retained Size（保留大小 / 支配内存，
/// 对标 dotMemory 的 Retained bytes）——若该对象被回收，可一并释放的内存总量。
/// 基于 Lengauer–Tarjan 算法；未挂到 GC 根的死对象按自身浅大小计。
/// </summary>
public sealed class DominatorAnalysis
{
    private readonly Dictionary<ulong, long> _retained = new();
    private readonly Dictionary<string, List<long>> _byType = new(StringComparer.Ordinal);
    private readonly List<(ulong Addr, string TypeName, long Size, long Retained)> _rows = new();

    public long ReachableCount { get; private set; }
    public long UnreachableCount { get; private set; }
    public long TotalRetained { get; private set; }

    /// <summary>在引用索引的基础上构建引用图并计算支配树。</summary>
    public static DominatorAnalysis Run(ReferenceIndex index, Action<long>? progress = null)
    {
        Graph g = Graph.BuildFrom(index);
        return Run(g, index, progress);
    }

    /// <summary>底层入口：对给定图计算支配树（供验证/测试复用）。</summary>
    public static DominatorAnalysis Run(Graph g, ReferenceIndex? indexForTypes = null, Action<long>? progress = null)
    {
        int n = g.NodeCount;
        var analysis = new DominatorAnalysis();

        // ---- 1. 可达性（从虚拟根 BFS）----
        bool[] reachable = ComputeReachable(g);
        progress?.Invoke(n / 4);

        // ---- 2. Lengauer-Tarjan 求支配树（仅可达子图）----
        int[] idom = ComputeIdom(g, reachable);
        progress?.Invoke(n / 2);

        // ---- 3. 支配树子树求和 = Retained Size ----
        long[] retained = SubtreeSums(g, idom, reachable);
        progress?.Invoke(3 * n / 4);

        // ---- 4. 汇总 ----
        for (int v = 0; v < n; v++)
        {
            if (v == g.Root) continue;
            ulong addr = g.Addresses[v];
            long r = reachable[v] ? retained[v] : g.ShallowSizes[v]; // 死对象：自身浅大小
            analysis._retained[addr] = r;
            if (reachable[v]) analysis.ReachableCount++; else analysis.UnreachableCount++;
            analysis.TotalRetained += r;

            if (indexForTypes != null)
            {
                string name = indexForTypes.GetBrief(addr).TypeName;
                if (!analysis._byType.TryGetValue(name, out List<long>? list))
                    analysis._byType[name] = list = new List<long>();
                list.Add(r);
                analysis._rows.Add((addr, name, g.ShallowSizes[v], r));
            }
        }
        progress?.Invoke(n);
        return analysis;
    }

    /// <summary>单对象 Retained Size。</summary>
    public long GetRetained(ulong addr) => _retained.TryGetValue(addr, out long r) ? r : 0;

    /// <summary>地址 → Retained Size 全量映射（供实例表 / 保留路径 / 合并树填充 Retained 用）。</summary>
    public IReadOnlyDictionary<ulong, long> RetainedByAddr => _retained;

    /// <summary>Retained Size 最大的前 limit 个对象（Dominators 视图，对标 dotMemory Dominators）。</summary>
    public IReadOnlyList<DominatorRow> GetTopDominators(int limit)
    {
        var list = new List<DominatorRow>(_rows.Count);
        foreach ((ulong addr, string name, long size, long retained) in _rows)
            list.Add(new DominatorRow { Address = addr, TypeName = name, Size = size, RetainedSize = retained });
        list.Sort((a, b) => b.RetainedSize.CompareTo(a.RetainedSize));
        return list.Count > limit ? list.GetRange(0, limit) : list;
    }

    /// <summary>按类型聚合的 Retained Size（对标 dotMemory Retained bytes 列）。</summary>
    public IReadOnlyList<RetainedInfo> GetRetainedByType()
    {
        var list = new List<RetainedInfo>(_byType.Count);
        foreach ((string name, List<long> sizes) in _byType)
            list.Add(new RetainedInfo { Name = name, RetainedSize = sizes.Sum() });
        list.Sort((a, b) => b.RetainedSize.CompareTo(a.RetainedSize));
        return list;
    }

    // ================= 校验（测试/验证用） =================

    /// <summary>
    /// 正确性校验，返回错误描述列表（空 = 通过）：
    /// 1) 小图（≤ 4000 节点）与暴力法（支配者集合不动点迭代）对拍支配树；
    /// 2) 不变式：可达节点 retained == 自身浅大小 + 支配树子树 retained 之和，且 ≥ 浅大小；
    /// 3) 虚拟根 retained == 全部可达对象浅大小之和。
    /// </summary>
    public static IReadOnlyList<string> Verify(Graph g)
    {
        var errors = new List<string>();
        bool[] reachable = ComputeReachable(g);
        int[] idom = ComputeIdom(g, reachable);
        long[] retained = SubtreeSums(g, idom, reachable);
        int n = g.NodeCount;

        // ---- 1) 暴力对拍（小图） ----
        if (n <= 4000)
        {
            HashSet<int>[] brute = BruteDominators(g, reachable);
            for (int v = 0; v < n; v++)
            {
                if (v == g.Root || !reachable[v]) continue;
                var viaIdom = new HashSet<int>();
                for (int u = v; u != g.Root && u != -1; u = idom[u]) viaIdom.Add(u);
                viaIdom.Add(g.Root);
                if (!viaIdom.SetEquals(brute[v]))
                    errors.Add($"节点 0x{g.Addresses[v]:X} 的支配者集合与暴力法不一致");
                if (errors.Count > 20) return errors;
            }
        }

        // ---- 2) 子树求和不变式（O(N)：构建支配树子节点链表后逐点核对） ----
        int[] head = new int[n], next = new int[n];
        Array.Fill(head, -1);
        for (int v = 0; v < n; v++)
        {
            if (v == g.Root || !reachable[v] || idom[v] < 0) continue;
            next[v] = head[idom[v]];
            head[idom[v]] = v;
        }
        for (int v = 0; v < n; v++)
        {
            if (v == g.Root || !reachable[v]) continue;
            long sum = g.ShallowSizes[v];
            for (int c = head[v]; c != -1; c = next[c]) sum += retained[c];
            if (sum != retained[v])
                errors.Add($"节点 0x{g.Addresses[v]:X} retained={retained[v]} ≠ 自身+子树 {sum}");
            if (retained[v] < g.ShallowSizes[v])
                errors.Add($"节点 0x{g.Addresses[v]:X} retained={retained[v]} < 浅大小 {g.ShallowSizes[v]}");
            if (errors.Count > 20) return errors;
        }

        // ---- 3) 根合计 ----
        long total = 0;
        for (int v = 0; v < n; v++)
            if (v != g.Root && reachable[v]) total += g.ShallowSizes[v];
        if (retained[g.Root] != total)
            errors.Add($"根 retained={retained[g.Root]} ≠ 可达浅大小合计 {total}");
        return errors;
    }

    /// <summary>暴力法求支配者集合（不动点迭代，O(N²) 级，仅小图验证用）。</summary>
    private static HashSet<int>[] BruteDominators(Graph g, bool[] reachable)
    {
        int n = g.NodeCount;
        var dom = new HashSet<int>[n];
        for (int v = 0; v < n; v++)
            dom[v] = reachable[v] ? new HashSet<int>(Enumerable.Range(0, n)) : new HashSet<int>();
        dom[g.Root] = new HashSet<int> { g.Root };
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int v = 0; v < n; v++)
            {
                if (v == g.Root || !reachable[v]) continue;
                HashSet<int>? inter = null;
                foreach (int p in g.Preds(v))
                {
                    if (!reachable[p]) continue;
                    if (inter == null) inter = new HashSet<int>(dom[p]);
                    else inter.IntersectWith(dom[p]);
                    if (inter.Count == 0) break;
                }
                inter ??= new HashSet<int>();
                inter.Add(v);
                if (!inter.SetEquals(dom[v])) { dom[v] = inter; changed = true; }
            }
        }
        return dom;
    }

    // ================= 核心算法 =================

    /// <summary>从虚拟根 BFS 标记可达节点。</summary>
    public static bool[] ComputeReachable(Graph g)
    {
        int n = g.NodeCount;
        var reachable = new bool[n];
        var queue = new Queue<int>();
        reachable[g.Root] = true;
        queue.Enqueue(g.Root);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int v in g.Succ(u))
                if (!reachable[v]) { reachable[v] = true; queue.Enqueue(v); }
        }
        return reachable;
    }

    /// <summary>支配树子树求和：retained[v] = 浅大小 + Σ retained[支配树子节点]。</summary>
    public static long[] SubtreeSums(Graph g, int[] idom, bool[] reachable)
    {
        int n = g.NodeCount;
        int[] head = new int[n], next = new int[n];
        Array.Fill(head, -1);
        for (int v = 0; v < n; v++)
        {
            if (v == g.Root || !reachable[v] || idom[v] < 0) continue;
            next[v] = head[idom[v]];
            head[idom[v]] = v;
        }
        long[] retained = new long[n];
        var post = new List<int>();
        var stack = new Stack<int>();
        stack.Push(g.Root);
        while (stack.Count > 0)
        {
            int u = stack.Pop();
            post.Add(u);
            for (int c = head[u]; c != -1; c = next[c]) stack.Push(c);
        }
        for (int i = post.Count - 1; i >= 0; i--)
        {
            int u = post[i];
            long sum = g.ShallowSizes[u];
            for (int c = head[u]; c != -1; c = next[c]) sum += retained[c];
            retained[u] = sum;
        }
        return retained;
    }

    /// <summary>
    /// Lengauer–Tarjan：返回 idom[]（下标），不可达为 -1，idom[root] = root。
    /// </summary>
    public static int[] ComputeIdom(Graph g, bool[] reachable)
    {
        int n = g.NodeCount;
        int[] semi = new int[n], idom = new int[n], parent = new int[n];
        int[] vertex = new int[n], label = new int[n], ancestor = new int[n];
        Array.Fill(semi, -1);
        Array.Fill(idom, -1);
        Array.Fill(parent, -1);
        Array.Fill(ancestor, -1);

        // predecessor（只保留可达边）
        List<int>[] preds = new List<int>[n];
        for (int i = 0; i < n; i++) preds[i] = new List<int>();
        for (int v = 0; v < n; v++)
        {
            if (v == g.Root || !reachable[v]) continue;
            foreach (int p in g.Preds(v))
                if (reachable[p]) preds[v].Add(p);
        }

        // DFS 编号（显式栈，避免深链递归）
        int counter = 0;
        var dfsStack = new Stack<(int Node, int Pred)>();
        dfsStack.Push((g.Root, -1));
        while (dfsStack.Count > 0)
        {
            (int u, int p) = dfsStack.Pop();
            if (semi[u] != -1) continue;
            semi[u] = counter;
            vertex[counter] = u;
            label[u] = u;
            parent[u] = p;
            counter++;
            foreach (int v in g.Succ(u))
                if (reachable[v] && semi[v] == -1) dfsStack.Push((v, u));
        }
        int dfsCount = counter;

        List<int>[] bucket = new List<int>[n];
        for (int i = 0; i < n; i++) bucket[i] = new List<int>();

        // Eval/Compress（迭代式路径压缩）
        int Eval(int v)
        {
            if (ancestor[v] == -1) return label[v];
            var path = new List<int>();
            int u = v;
            while (ancestor[u] != -1 && ancestor[ancestor[u]] != -1)
            {
                path.Add(u);
                u = ancestor[u];
            }
            for (int i = path.Count - 1; i >= 0; i--)
            {
                int x = path[i];
                int a = ancestor[x];
                if (semi[label[a]] < semi[label[x]]) label[x] = label[a];
                ancestor[x] = ancestor[a];
            }
            return label[v];
        }

        // 逆 DFS 序求 semi / idom
        for (int i = dfsCount - 1; i >= 1; i--)
        {
            int w = vertex[i];
            foreach (int v in preds[w])
            {
                int u = Eval(v);
                if (semi[u] < semi[w]) semi[w] = semi[u];
            }
            bucket[vertex[semi[w]]].Add(w);
            if (parent[w] != -1)
            {
                ancestor[w] = parent[w];
                foreach (int v in bucket[parent[w]])
                {
                    int u = Eval(v);
                    idom[v] = semi[u] < semi[v] ? u : parent[w];
                }
                bucket[parent[w]].Clear();
            }
        }
        for (int i = 1; i < dfsCount; i++)
        {
            int w = vertex[i];
            if (idom[w] != vertex[semi[w]]) idom[w] = idom[idom[w]];
        }
        idom[g.Root] = g.Root;
        return idom;
    }

    // ================= 图结构 =================

    /// <summary>引用图（地址 ↔ 下标），含虚拟根。供支配树分析与验证复用。</summary>
    public sealed class Graph
    {
        public int Root;                       // 虚拟根下标
        public int NodeCount;
        public ulong[] Addresses = Array.Empty<ulong>();
        public long[] ShallowSizes = Array.Empty<long>();
        private List<int>[] _succ = Array.Empty<List<int>>();
        private List<int>[] _preds = Array.Empty<List<int>>();

        public IReadOnlyList<int> Succ(int u) => _succ[u];
        public IReadOnlyList<int> Preds(int u) => _preds[u];

        /// <summary>按显式边构造图（测试/验证用）。虚拟根浅大小恒为 0。</summary>
        public static Graph FromEdges(int nodeCount, int root, long[] shallow, IEnumerable<(int From, int To)> edges)
        {
            shallow[root] = 0; // 虚拟根不是真实对象
            var g = new Graph { NodeCount = nodeCount, Root = root, ShallowSizes = shallow, Addresses = new ulong[nodeCount] };
            for (int i = 0; i < nodeCount; i++) g.Addresses[i] = (ulong)i;
            g._succ = new List<int>[nodeCount];
            g._preds = new List<int>[nodeCount];
            for (int k = 0; k < nodeCount; k++) { g._succ[k] = new List<int>(); g._preds[k] = new List<int>(); }
            foreach ((int from, int to) in edges)
            {
                g._succ[from].Add(to);
                g._preds[to].Add(from);
            }
            return g;
        }

        /// <summary>从引用索引建图：对象为节点，引用为边；虚拟根连向全部 GC 根对象。</summary>
        public static Graph BuildFrom(ReferenceIndex index)
        {
            var addrSet = new HashSet<ulong>();
            foreach (ulong r in index.RootAddresses) addrSet.Add(r);
            foreach ((ulong child, List<ReferenceIndex.ReferrerEdge> edges) in index.Referrers)
            {
                addrSet.Add(child);
                foreach (ReferenceIndex.ReferrerEdge e in edges) addrSet.Add(e.Parent);
            }

            var g = new Graph { NodeCount = addrSet.Count + 1 };
            g.Root = addrSet.Count;
            g.Addresses = new ulong[g.NodeCount];
            g.ShallowSizes = new long[g.NodeCount];
            var indexOf = new Dictionary<ulong, int>(addrSet.Count + 1);
            int i = 0;
            foreach (ulong a in addrSet)
            {
                indexOf[a] = i;
                g.Addresses[i] = a;
                g.ShallowSizes[i] = index.GetBrief(a).Size;
                i++;
            }
            g._succ = new List<int>[g.NodeCount];
            g._preds = new List<int>[g.NodeCount];
            for (int k = 0; k < g.NodeCount; k++)
            {
                g._succ[k] = new List<int>();
                g._preds[k] = new List<int>();
            }

            void AddEdge(int from, int to)
            {
                g._succ[from].Add(to);
                g._preds[to].Add(from);
            }

            foreach (ulong r in index.RootAddresses)
                AddEdge(g.Root, indexOf[r]);
            foreach ((ulong child, List<ReferenceIndex.ReferrerEdge> edges) in index.Referrers)
                foreach (ReferenceIndex.ReferrerEdge e in edges)
                    AddEdge(indexOf[e.Parent], indexOf[child]);
            return g;
        }
    }
}
