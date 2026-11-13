using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using HeapLens.Core;

namespace HeapLens.App;

/// <summary>
/// 主窗口 ViewModel：进程选择、快照捕获、堆分析、调用链（引用树）、快照对比。
/// 注意：所有 ObservableCollection 的修改都必须在 UI 线程执行（WPF 跨线程改集合会抛异常）。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private HeapAnalyzer? _analyzer;
    private ReferenceIndex? _index;
    private DominatorAnalysis? _dom;
    /// <summary>调用链计算序号令牌：并发触发时只采纳最后一次结果（Bug 3 修复）。</summary>
    private int _chainSeq;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- 进程选择 ----
    public ObservableCollection<ProcessScanner.DotNetProcess> Processes { get; } = new();
    private ProcessScanner.DotNetProcess? _selectedProcess;
    public ProcessScanner.DotNetProcess? SelectedProcess { get => _selectedProcess; set => Set(ref _selectedProcess, value); }

    // ---- 状态 ----
    private string _status = "就绪：选择 .NET 进程捕获快照，或打开已有 dump 文件";
    public string Status { get => _status; set => Set(ref _status, value); }
    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(CanAct)); } }
    public bool CanAct => !_isBusy;

    // ---- 概览 ----
    private HeapOverview? _overview;
    public HeapOverview? Overview { get => _overview; set => Set(ref _overview, value); }
    public ObservableCollection<SegmentStat> Segments { get; } = new();
    public ObservableCollection<GenBar> GenBars { get; } = new();

    // ---- 类型分布 ----
    public ObservableCollection<TypeStat> Types { get; } = new();
    private List<TypeStat> _allTypes = new();
    private string _typeFilter = "";
    /// <summary>类型过滤器：支持子串（默认）、通配符 * ?（如 System.Collections.*）、正则（re: 开头）。</summary>
    public string TypeFilter { get => _typeFilter; set { if (Set(ref _typeFilter, value)) ApplyTypeFilter(); } }
    private bool _namespaceGrouping;
    /// <summary>命名空间分组开关：按命名空间聚合类型统计（对标 dotMemory 按命名空间分组）。</summary>
    public bool NamespaceGrouping { get => _namespaceGrouping; set { if (Set(ref _namespaceGrouping, value)) ApplyTypeFilter(); } }
    private TypeStat? _selectedType;
    public TypeStat? SelectedType { get => _selectedType; set => Set(ref _selectedType, value); }

    // ---- 实例与保留路径 ----
    public ObservableCollection<InstanceInfo> Instances { get; } = new();
    private InstanceInfo? _selectedInstance;
    public InstanceInfo? SelectedInstance { get => _selectedInstance; set { if (Set(ref _selectedInstance, value)) _ = ShowRetentionPathAsync(); } }
    public ObservableCollection<RetentionNode> RetentionNodes { get; } = new();
    private string _retentionTitle = "选择左侧实例查看保留路径";
    public string RetentionTitle { get => _retentionTitle; set => Set(ref _retentionTitle, value); }

    // ---- 调用链（引用树 / 类型持有链） ----
    public ObservableCollection<ChainTreeNode> ChainRoots { get; } = new();
    public ObservableCollection<ChainTreeNode> MergedChainRoots { get; } = new();
    public ObservableCollection<InstanceInfo> ChainInstances { get; } = new();
    private InstanceInfo? _chainSelectedInstance;
    public InstanceInfo? ChainSelectedInstance { get => _chainSelectedInstance; set { if (Set(ref _chainSelectedInstance, value)) { if (value != null) ShowChainForAddress(value.Address); } } }
    public ObservableCollection<ChainPathVm> ChainPaths { get; } = new();
    public ObservableCollection<ReferenceIndex.ReferrerInfo> ChainReferrers { get; } = new();
    private string _chainTitle = "加载 dump 后：双击左侧类型 → 双击实例，或点击/展开右侧引用树节点，即时查看完整调用链";
    public string ChainTitle { get => _chainTitle; set => Set(ref _chainTitle, value); }
    private string _mergedChainTitle = "双击左侧类型，穿透显示该类型所有实例的持有链（合并树，对标 dotMemory）";
    public string MergedChainTitle { get => _mergedChainTitle; set => Set(ref _mergedChainTitle, value); }

    // ---- 支配对象（Dominators） ----
    public ObservableCollection<DominatorRow> Dominators { get; } = new();
    private string _dominatorsTitle = "加载 dump 后自动计算支配对象（按 Retained Size 降序 Top 500）";
    public string DominatorsTitle { get => _dominatorsTitle; set => Set(ref _dominatorsTitle, value); }

    // ---- 字符串搜索 ----
    public ObservableCollection<StringGroup> StringResults { get; } = new();
    private string _stringSearchKeyword = "";
    public string StringSearchKeyword { get => _stringSearchKeyword; set => Set(ref _stringSearchKeyword, value); }
    private string _stringSearchTitle = "输入关键字搜索字符串内容（留空则按总大小列出 Top 组），点击「搜索」执行";
    public string StringSearchTitle { get => _stringSearchTitle; set => Set(ref _stringSearchTitle, value); }

    // ---- 快照对比 ----
    private string _diffPathA = "";
    public string DiffPathA { get => _diffPathA; set => Set(ref _diffPathA, value); }
    private string _diffPathB = "";
    public string DiffPathB { get => _diffPathB; set => Set(ref _diffPathB, value); }
    public ObservableCollection<DiffRow> DiffRows { get; } = new();

    // ---- 命令 ----
    public ICommand RefreshProcessesCommand { get; }
    public ICommand CaptureSnapshotCommand { get; }
    public ICommand OpenDumpCommand { get; }
    public ICommand OpenDiffACommand { get; }
    public ICommand OpenDiffBCommand { get; }
    public ICommand RunDiffCommand { get; }
    public ICommand ShowInstancesCommand { get; }
    public ICommand SearchStringsCommand { get; }

    public MainViewModel()
    {
        RefreshProcessesCommand = new RelayCommand(async _ => await RefreshProcessesAsync());
        CaptureSnapshotCommand = new RelayCommand(async _ => await CaptureSnapshotAsync(), _ => CanAct && SelectedProcess != null);
        OpenDumpCommand = new RelayCommand(async _ => await OpenDumpAsync(), _ => CanAct);
        OpenDiffACommand = new RelayCommand(_ => PickDiffPath(isA: true));
        OpenDiffBCommand = new RelayCommand(_ => PickDiffPath(isA: false));
        RunDiffCommand = new RelayCommand(async _ => await RunDiffAsync(), _ => CanAct);
        ShowInstancesCommand = new RelayCommand(async p => await ShowInstancesAsync(p as TypeStat), _ => CanAct);
        SearchStringsCommand = new RelayCommand(async _ => await SearchStringsAsync(), _ => CanAct);
        _ = RefreshProcessesAsync();
    }

    // ---------- 进程 ----------

    public async Task RefreshProcessesAsync()
    {
        Status = "正在扫描 .NET 进程…";
        try
        {
            IReadOnlyList<ProcessScanner.DotNetProcess> list = await Task.Run(() => ProcessScanner.ListDotNetProcesses());
            Processes.Clear();
            foreach (var p in list) Processes.Add(p);
            Status = $"发现 {Processes.Count} 个 .NET 进程";
        }
        catch (Exception ex) { Status = "进程扫描失败: " + ex.Message; }
    }

    // ---------- 捕获快照 ----------

    private async Task CaptureSnapshotAsync()
    {
        if (SelectedProcess == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存内存快照 (dump)",
            FileName = $"{SelectedProcess.Name}_{SelectedProcess.Pid}_{DateTime.Now:yyyyMMdd_HHmmss}.dmp",
            Filter = "内存转储 (*.dmp)|*.dmp",
        };
        if (dlg.ShowDialog() != true) return;

        int pid = SelectedProcess.Pid;
        string path = dlg.FileName;
        IsBusy = true;
        try
        {
            Status = $"正在捕获 pid={pid} 的内存快照…";
            await Task.Run(() => SnapshotCapture.Capture(pid, path));
            await LoadAnalyzerAsync(path);
        }
        catch (Exception ex)
        {
            Status = "出错: " + ex.Message;
            ShowError("捕获快照失败", ex);
        }
        finally { IsBusy = false; }
    }

    // ---------- 打开 dump ----------

    private async Task OpenDumpAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开内存转储 (dump)",
            Filter = "内存转储 (*.dmp)|*.dmp|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        await LoadAnalyzerAsync(dlg.FileName);
    }

    /// <summary>
    /// 加载并分析 dump。重活全部在后台线程完成，
    /// 集合/属性更新统一回到 UI 线程（WPF 禁止跨线程修改绑定集合）。
    /// </summary>
    private async Task LoadAnalyzerAsync(string path)
    {
        IsBusy = true;
        Status = $"正在分析 {Path.GetFileName(path)}…";
        try
        {
            (HeapAnalyzer an, HeapOverview ov, List<TypeStat> types, List<SegmentStat> segs, ReferenceIndex index, DominatorAnalysis dom,
                IReadOnlyList<DominatorRow> topDoms) =
                await Task.Run(() =>
                {
                    var an = HeapAnalyzer.OpenDump(path);
                    var ov = an.GetOverview();
                    var types = an.GetTypeStats().ToList();
                    var segs = an.GetSegments().ToList();
                    Status = "正在构建引用索引（调用链，大堆约需几十秒）…";
                    var index = ReferenceIndex.Build(an.Heap,
                        n => Status = $"正在构建引用索引…已处理 {n:N0} 个对象");
                    Status = "正在计算支配树 Retained Size…";
                    var dom = DominatorAnalysis.Run(index,
                        n => Status = $"正在计算 Retained Size…{n:N0}");
                    var topDoms = dom.GetTopDominators(500);
                    return (an, ov, types, segs, index, dom, topDoms);
                });

            // ---- 以下都在 UI 线程 ----
            HeapAnalyzer? old = _analyzer;
            _analyzer = an;
            _index = index;
            _dom = dom;
            old?.Dispose();

            Overview = ov;
            _allTypes = types;
            // 应用 Retained Size（支配树保留大小，对标 dotMemory）
            var retainedMap = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (RetainedInfo r in dom.GetRetainedByType()) retainedMap[r.Name] = r.RetainedSize;
            foreach (TypeStat t in types)
                if (retainedMap.TryGetValue(t.Name, out long rs)) t.RetainedSize = rs;
            ApplyTypeFilter();

            Segments.Clear();
            foreach (var s in segs) Segments.Add(s);

            GenBars.Clear();
            long total = Math.Max(1, ov.Gen0Size + ov.Gen1Size + ov.Gen2Size + ov.LohSize + ov.PohSize);
            GenBars.Add(new GenBar("Gen0", ov.Gen0Size, total, "#2563EB"));
            GenBars.Add(new GenBar("Gen1", ov.Gen1Size, total, "#7C3AED"));
            GenBars.Add(new GenBar("Gen2", ov.Gen2Size, total, "#DB2777"));
            GenBars.Add(new GenBar("LOH", ov.LohSize, total, "#EA580C"));
            GenBars.Add(new GenBar("POH", ov.PohSize, total, "#059669"));

            Instances.Clear();
            RetentionNodes.Clear();
            ChainInstances.Clear();
            ChainPaths.Clear();
            ChainReferrers.Clear();
            MergedChainRoots.Clear();
            Dominators.Clear();
            foreach (DominatorRow d in topDoms) Dominators.Add(d);
            DominatorsTitle = $"支配对象：按 Retained Size 降序 Top {Dominators.Count:N0}（Retained = 该对象被释放时可回收的内存总量）";
            StringResults.Clear();

            ChainRoots.Clear();
            foreach (ReferenceIndex.RootGroupInfo g in index.GetRootGroups(200))
                ChainRoots.Add(ChainTreeNode.ForRootGroup(g));
            ChainTitle = $"共 {index.RootCount:N0} 个 GC 根；点击/展开引用树节点或选择实例，即时查看调用链";
            MergedChainTitle = "双击左侧类型，穿透显示该类型所有实例的持有链（合并树，对标 dotMemory）";

            Status = $"已加载 {Path.GetFileName(path)}：{ov.ObjectCount:N0} 个对象，{types.Count} 种类型，" +
                     $"{index.RootCount:N0} 个 GC 根，{index.EdgeCount:N0} 条引用边，" +
                     $"Retained 已就绪（可达 {dom.ReachableCount:N0} / 死对象 {dom.UnreachableCount:N0}）";
        }
        catch (Exception ex)
        {
            Status = "出错: " + ex.Message;
            ShowError($"加载 {Path.GetFileName(path)} 失败", ex);
        }
        finally { IsBusy = false; }
    }

    // ---------- 类型过滤 / 实例 ----------

    private void ApplyTypeFilter()
    {
        Types.Clear();
        IEnumerable<TypeStat> q = _allTypes.Where(t => MatchesFilter(t.Name, TypeFilter));
        if (NamespaceGrouping)
        {
            // 命名空间分组：按命名空间聚合（对标 dotMemory），分组行 IsNamespaceGroup = true
            var groups = new Dictionary<string, TypeStat>(StringComparer.Ordinal);
            foreach (TypeStat t in q)
            {
                string ns = GetNamespace(t.Name);
                if (!groups.TryGetValue(ns, out TypeStat? g))
                    groups[ns] = g = new TypeStat { Name = ns, IsNamespaceGroup = true };
                g.Count += t.Count;
                g.TotalSize += t.TotalSize;
                g.RetainedSize += t.RetainedSize;
                g.Gen0Count += t.Gen0Count;
                g.Gen1Count += t.Gen1Count;
                g.Gen2Count += t.Gen2Count;
                g.LohCount += t.LohCount;
                g.PohCount += t.PohCount;
                if (t.MaxSize > g.MaxSize) g.MaxSize = t.MaxSize;
            }
            List<TypeStat> agg = groups.Values.ToList();
            agg.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));
            foreach (var t in agg.Take(2000)) Types.Add(t);
        }
        else
        {
            foreach (var t in q.Take(2000)) Types.Add(t);
        }
    }

    /// <summary>类型过滤器匹配：默认子串（忽略大小写）；支持通配符 * ?；re: 开头为正则。</summary>
    private static bool MatchesFilter(string name, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        try
        {
            if (filter.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
                return Regex.IsMatch(name, filter[3..], RegexOptions.IgnoreCase);
            if (filter.Contains('*') || filter.Contains('?'))
            {
                string pattern = "^" + Regex.Escape(filter).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase);
            }
        }
        catch (ArgumentException) { /* 正则非法时退化为子串匹配 */ }
        return name.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取类型的命名空间（处理泛型参数 / 嵌套类型；无命名空间归为 &lt;global&gt;）。</summary>
    private static string GetNamespace(string typeName)
    {
        string baseName = typeName;
        int b = baseName.IndexOf('[');
        if (b >= 0) baseName = baseName[..b];
        int plus = baseName.IndexOf('+');
        if (plus >= 0) baseName = baseName[..plus];
        int dot = baseName.LastIndexOf('.');
        return dot > 0 ? baseName[..dot] : "<global>";
    }

    private async Task ShowInstancesAsync(TypeStat? type)
    {
        if (_analyzer == null || type == null) return;
        if (type.IsNamespaceGroup) { Status = $"{type.Name} 是命名空间分组行，请关闭分组或筛选到具体类型后查看实例"; return; }
        SelectedType = type;
        try
        {
            IReadOnlyList<InstanceInfo> insts = _index != null
                ? await Task.Run(() => _index.GetInstances(type.Name, 500))
                : await Task.Run(() => _analyzer.GetInstances(type.Name, 500));
            FillRetained(insts);
            Instances.Clear();
            foreach (var i in insts) Instances.Add(i);
            Status = $"{type.Name}：{insts.Count} 个实例（按大小降序）";
        }
        catch (Exception ex) { Status = "出错: " + ex.Message; }
    }

    /// <summary>用支配树结果填充实例的 Retained Size（单对象 Retained 显示）。</summary>
    private void FillRetained(IEnumerable<InstanceInfo> insts)
    {
        if (_dom == null) return;
        foreach (InstanceInfo i in insts) i.RetainedSize = _dom.GetRetained(i.Address);
    }

    /// <summary>用支配树结果填充保留路径节点的 Retained Size。</summary>
    private void FillRetained(IEnumerable<RetentionNode> nodes)
    {
        if (_dom == null) return;
        foreach (RetentionNode n in nodes) n.RetainedSize = _dom.GetRetained(n.Address);
    }

    // ---------- 字符串搜索（对标 dotMemory Strings 视图） ----------

    private async Task SearchStringsAsync()
    {
        if (_analyzer == null) { StringSearchTitle = "请先加载 dump 文件"; return; }
        string keyword = StringSearchKeyword ?? "";
        StringSearchTitle = "正在扫描字符串…";
        try
        {
            StringSearchResult result = await Task.Run(() => _analyzer.SearchStrings(keyword));
            StringResults.Clear();
            foreach (StringGroup g in result.Groups) StringResults.Add(g);
            string tail = result.Truncated ? "，已达扫描上限" : "";
            StringSearchTitle = keyword.Length > 0
                ? $"包含「{keyword}」：{result.MatchedCount:N0} 个实例、{result.Groups.Count:N0} 组（扫描 {result.ScannedCount:N0} 个字符串{tail}）"
                : $"全部字符串：{result.MatchedCount:N0} 个实例、{result.Groups.Count:N0} 组（按总大小降序{tail}）";
        }
        catch (Exception ex) { StringSearchTitle = "搜索失败: " + ex.Message; }
    }

    private async Task ShowRetentionPathAsync()
    {
        if (_index == null || SelectedInstance == null) return;
        ulong addr = SelectedInstance.Address;
        RetentionNodes.Clear();
        RetentionTitle = "正在计算调用链…";
        try
        {
            IReadOnlyList<RetentionPath> paths = await Task.Run(() => _index.GetRetentionPaths(addr, 3));
            if (paths.Count == 0)
            {
                RetentionTitle = $"0x{addr:X12}：未找到到 GC 根的引用链（对象可能不可达或搜索超限）";
                return;
            }
            FillRetained(paths[0].Nodes);
            foreach (RetentionNode n in paths[0].Nodes) RetentionNodes.Add(n);
            RetentionTitle = $"调用链（最短保留路径）：GC 根（{paths[0].RootKind}） → 目标，共 {paths[0].Nodes.Count} 层" +
                             (paths.Count > 1 ? $"；另有 {paths.Count - 1} 条备选路径（见「调用链」页）" : "");
        }
        catch (Exception ex) { RetentionTitle = "分析失败: " + ex.Message; }
    }

    // ---------- 调用链（引用树） ----------

    /// <summary>双击类型 → 列出实例并构建该类型所有实例的持有链合并树（对标 dotMemory）。</summary>
    public async Task LoadChainInstancesAsync(TypeStat type)
    {
        if (_index == null) return;
        if (type.IsNamespaceGroup) { MergedChainTitle = $"{type.Name} 是命名空间分组行，请关闭分组或筛选到具体类型后查看持有链"; return; }
        MergedChainTitle = $"正在构建 {type.Name} 的持有链合并树…";
        try
        {
            IReadOnlyDictionary<ulong, long>? retained = _dom?.RetainedByAddr;
            (IReadOnlyList<InstanceInfo> insts, ReferenceIndex.MergedTreeResult tree) = await Task.Run(
                () => (_index.GetInstances(type.Name, 500),
                       _index.GetMergedRetentionTree(type.Name, 200, 2, retained)));

            FillRetained(insts);
            ChainInstances.Clear();
            foreach (var i in insts) ChainInstances.Add(i);

            MergedChainRoots.Clear();
            MergedChainRoots.Add(ToTreeNode(tree.Root));
            MergedChainTitle = $"{type.Name}：{tree.LinkedInstances}/{tree.TotalInstances} 个实例、{tree.TotalPaths} 条持有链（按持有者类型+字段合并聚合，含 Retained）";
            ChainTitle = $"{type.Name}：{insts.Count} 个实例 —— 选择实例查看单条调用链，或查看右侧合并持有链树";
        }
        catch (Exception ex) { MergedChainTitle = "构建失败: " + ex.Message; }
    }

    /// <summary>合并树节点 → 树视图节点（子节点全部预填，无需懒加载）。</summary>
    private static ChainTreeNode ToTreeNode(MergedChainNode n)
    {
        string title = n.TypeName == "GC 根"
            ? "GC 根"
            : $"{n.TypeName}{(n.Field != null ? $"（经 .{n.Field} 持有）" : "")}  —  经此持有 {n.TargetCount} 个目标实例 · 本层 {n.ObjectCount} 个对象 · {FormatBytes(n.TotalSize)} · 保留 {FormatBytes(n.RetainedSize)}";
        var node = new ChainTreeNode
        {
            Kind = ChainNodeKind.Merged,
            Title = title,
            Loaded = true,
        };
        foreach (MergedChainNode c in n.Children) node.Children.Add(ToTreeNode(c));
        return node;
    }

    /// <summary>
    /// 即时显示某对象的调用链（保留路径）+ 全部直接引用者。
    /// 计算在后台线程执行（BFS/DFS 最坏可达数十万状态），UI 线程只做集合更新；
    /// 用序号令牌丢弃过期结果——连续快速切换实例时只显示最后一次的调用链（Bug 3 修复）。
    /// </summary>
    public void ShowChainForAddress(ulong addr)
    {
        ReferenceIndex? index = _index;
        if (index == null) return;
        int seq = Interlocked.Increment(ref _chainSeq);
        ChainTitle = $"正在计算 0x{addr:X12} 的调用链…";
        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<RetentionPath> paths = index.GetRetentionPaths(addr, 3);
                IReadOnlyList<ReferenceIndex.ReferrerInfo> refs = index.GetReferrerInfos(addr);
                string label = index.GetBrief(addr).TypeName;

                // 回 UI 线程更新集合；若期间用户已选择其他对象（seq 过期）则丢弃本次结果
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (seq != _chainSeq) return;
                    FillRetained(paths.SelectMany(p => p.Nodes));
                    ChainPaths.Clear();
                    for (int i = 0; i < paths.Count; i++)
                        ChainPaths.Add(new ChainPathVm(i + 1, paths[i]));
                    ChainReferrers.Clear();
                    foreach (var r in refs) ChainReferrers.Add(r);
                    ChainTitle = paths.Count == 0
                        ? $"0x{addr:X12}（{label}）：未找到到 GC 根的引用链（对象可能不可达）"
                        : $"0x{addr:X12}（{label}）的调用链：{paths.Count} 条保留路径（根类型 {paths[0].RootKind}），另有 {refs.Count} 个直接引用者";
                });
            }
            catch (Exception ex)
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (seq == _chainSeq) ChainTitle = "分析失败: " + ex.Message;
                });
            }
        });
    }

    /// <summary>引用树节点首次展开时懒加载下级引用。</summary>
    public void LoadChildren(ChainTreeNode node)
    {
        if (node.Loaded || _index == null) return;
        node.Loaded = true;
        try
        {
            switch (node.Kind)
            {
                case ChainNodeKind.RootGroup:
                    if (node.RootAddresses == null) break;
                    foreach (ulong addr in node.RootAddresses)
                    {
                        ReferenceIndex.NodeBrief b = _index.GetBrief(addr);
                        node.Children.Add(new ChainTreeNode
                        {
                            Kind = ChainNodeKind.Object,
                            Address = addr,
                            Title = $"{b.TypeName}  [{FormatBytes(b.Size)}]  0x{addr:X12}",
                        });
                    }
                    if (node.TotalCount > node.RootAddresses.Count)
                        node.Children.Add(ChainTreeNode.ForTruncated($"… 该组另有 {node.TotalCount - node.RootAddresses.Count:N0} 个根未显示"));
                    break;

                case ChainNodeKind.Object:
                    IReadOnlyList<ReferenceIndex.ChildInfo> kids = _index.GetChildren(node.Address, 301);
                    bool more = kids.Count > 300;
                    for (int i = 0; i < (more ? 300 : kids.Count); i++)
                    {
                        ReferenceIndex.ChildInfo c = kids[i];
                        string field = string.IsNullOrEmpty(c.Field) ? "" : $".{c.Field}  ";
                        node.Children.Add(new ChainTreeNode
                        {
                            Kind = ChainNodeKind.Object,
                            Address = c.Address,
                            Title = $"{field}{c.TypeName}  [{FormatBytes(c.Size)}]  0x{c.Address:X12}",
                        });
                    }
                    if (more) node.Children.Add(ChainTreeNode.ForTruncated("… 该对象还有更多引用对象，仅显示前 300 个"));
                    break;
            }
        }
        catch (Exception ex) { node.Children.Add(ChainTreeNode.ForTruncated("展开失败: " + ex.Message)); }
    }

    /// <summary>树节点选中 → 右侧显示其调用链。</summary>
    public void OnChainNodeSelected(ChainTreeNode node)
    {
        if (node.Kind == ChainNodeKind.Object) ShowChainForAddress(node.Address);
    }

    // ---------- 快照对比 ----------

    private void PickDiffPath(bool isA)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择快照 (dump)", Filter = "内存转储 (*.dmp)|*.dmp" };
        if (dlg.ShowDialog() != true) return;
        if (isA) DiffPathA = dlg.FileName; else DiffPathB = dlg.FileName;
    }

    private async Task RunDiffAsync()
    {
        if (!File.Exists(DiffPathA) || !File.Exists(DiffPathB))
        { Status = "请先选择两个快照文件"; return; }
        IsBusy = true;
        Status = "正在对比两个快照…";
        try
        {
            IReadOnlyList<DiffRow> rows = await Task.Run(() =>
            {
                using var a = HeapAnalyzer.OpenDump(DiffPathA);
                using var b = HeapAnalyzer.OpenDump(DiffPathB);
                return DiffEngine.Diff(a.GetTypeStats(), b.GetTypeStats());
            });
            DiffRows.Clear();
            foreach (var r in rows.Take(500)) DiffRows.Add(r);
            Status = $"对比完成：{Math.Min(rows.Count, 500)} 个类型有变化";
        }
        catch (Exception ex) { Status = "出错: " + ex.Message; ShowError("快照对比失败", ex); }
        finally { IsBusy = false; }
    }

    // ---------- 通用 ----------

    private static void ShowError(string title, Exception ex)
    {
        try { MessageBox.Show($"{title}：\n\n{ex.Message}", "HeapLens", MessageBoxButton.OK, MessageBoxImage.Error); }
        catch { /* 无 UI 环境时忽略 */ }
    }

    internal static string FormatBytes(long b)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double v = Math.Abs(b);
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{(b < 0 ? "-" : "")}{v:0.##} {units[u]}";
    }

    public void Close()
    {
        Interlocked.Increment(ref _chainSeq); // 作废未完成的调用链计算
        _analyzer?.Dispose();
        _analyzer = null;
        _index = null;
        _dom = null;
    }

    // ---- 绑定辅助 ----
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>代龄分布条目（图表用）。</summary>
    public sealed record GenBar(string Label, long Size, long Total, string Color)
    {
        public double Width => Math.Max(2, 220.0 * Size / Total);
    }
}

// ---------- 调用链树的视图模型 ----------

public enum ChainNodeKind { RootGroup, Object, Merged, Truncated }

/// <summary>引用树节点（GC 根分组 / 对象 / 截断占位）。</summary>
public sealed class ChainTreeNode
{
    public ChainNodeKind Kind { get; init; }
    public string Title { get; init; } = "";
    public ulong Address { get; init; }
    public IReadOnlyList<ulong>? RootAddresses { get; init; }
    public int TotalCount { get; init; }
    public ObservableCollection<ChainTreeNode> Children { get; } = new();
    public bool Loaded { get; set; }

    public static ChainTreeNode ForRootGroup(ReferenceIndex.RootGroupInfo g) => new()
    {
        Kind = ChainNodeKind.RootGroup,
        Title = $"GC 根 · {g.Kind}（{g.TotalCount:N0} 个）",
        RootAddresses = g.Addresses,
        TotalCount = g.TotalCount,
    };

    public static ChainTreeNode ForTruncated(string title) => new()
    {
        Kind = ChainNodeKind.Truncated,
        Title = title,
        Loaded = true,
    };
}

/// <summary>一条调用链（保留路径）展示。</summary>
public sealed class ChainPathVm
{
    public ChainPathVm(int index, RetentionPath path)
    {
        Title = $"路径 {index}：GC 根（{path.RootKind}） → 目标，共 {path.Nodes.Count} 层";
        foreach (var n in path.Nodes) Nodes.Add(n);
    }
    public string Title { get; }
    public ObservableCollection<RetentionNode> Nodes { get; } = new();
}

/// <summary>极简 ICommand 实现。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;
    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    { _execute = execute; _canExecute = canExecute; }
    public bool CanExecute(object? p) => _canExecute?.Invoke(p) ?? true;
    public void Execute(object? p) => _execute(p);
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
