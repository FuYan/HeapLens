using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
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
    public string TypeFilter { get => _typeFilter; set { if (Set(ref _typeFilter, value)) ApplyTypeFilter(); } }
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

    public MainViewModel()
    {
        RefreshProcessesCommand = new RelayCommand(async _ => await RefreshProcessesAsync());
        CaptureSnapshotCommand = new RelayCommand(async _ => await CaptureSnapshotAsync(), _ => CanAct && SelectedProcess != null);
        OpenDumpCommand = new RelayCommand(async _ => await OpenDumpAsync(), _ => CanAct);
        OpenDiffACommand = new RelayCommand(_ => PickDiffPath(isA: true));
        OpenDiffBCommand = new RelayCommand(_ => PickDiffPath(isA: false));
        RunDiffCommand = new RelayCommand(async _ => await RunDiffAsync(), _ => CanAct);
        ShowInstancesCommand = new RelayCommand(async p => await ShowInstancesAsync(p as TypeStat), _ => CanAct);
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
            (HeapAnalyzer an, HeapOverview ov, List<TypeStat> types, List<SegmentStat> segs, ReferenceIndex index, DominatorAnalysis dom) =
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
                    return (an, ov, types, segs, index, dom);
                });

            // ---- 以下都在 UI 线程 ----
            HeapAnalyzer? old = _analyzer;
            _analyzer = an;
            _index = index;
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
        IEnumerable<TypeStat> q = _allTypes;
        if (!string.IsNullOrWhiteSpace(TypeFilter))
            q = q.Where(t => t.Name.Contains(TypeFilter, StringComparison.OrdinalIgnoreCase));
        foreach (var t in q.Take(2000)) Types.Add(t);
    }

    private async Task ShowInstancesAsync(TypeStat? type)
    {
        if (_analyzer == null || type == null) return;
        SelectedType = type;
        try
        {
            IReadOnlyList<InstanceInfo> insts = _index != null
                ? await Task.Run(() => _index.GetInstances(type.Name, 500))
                : await Task.Run(() => _analyzer.GetInstances(type.Name, 500));
            Instances.Clear();
            foreach (var i in insts) Instances.Add(i);
            Status = $"{type.Name}：{insts.Count} 个实例（按大小降序）";
        }
        catch (Exception ex) { Status = "出错: " + ex.Message; }
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
        MergedChainTitle = $"正在构建 {type.Name} 的持有链合并树…";
        try
        {
            (IReadOnlyList<InstanceInfo> insts, ReferenceIndex.MergedTreeResult tree) = await Task.Run(
                () => (_index.GetInstances(type.Name, 500), _index.GetMergedRetentionTree(type.Name, 200, 2)));

            ChainInstances.Clear();
            foreach (var i in insts) ChainInstances.Add(i);

            MergedChainRoots.Clear();
            MergedChainRoots.Add(ToTreeNode(tree.Root));
            MergedChainTitle = $"{type.Name}：{tree.LinkedInstances}/{tree.TotalInstances} 个实例、{tree.TotalPaths} 条持有链（按持有者类型+字段合并聚合）";
            ChainTitle = $"{type.Name}：{insts.Count} 个实例 —— 选择实例查看单条调用链，或查看右侧合并持有链树";
        }
        catch (Exception ex) { MergedChainTitle = "构建失败: " + ex.Message; }
    }

    /// <summary>合并树节点 → 树视图节点（子节点全部预填，无需懒加载）。</summary>
    private static ChainTreeNode ToTreeNode(MergedChainNode n)
    {
        string title = n.TypeName == "GC 根"
            ? "GC 根"
            : $"{n.TypeName}{(n.Field != null ? $"（经 .{n.Field} 持有）" : "")}  —  经此持有 {n.TargetCount} 个目标实例 · 本层 {n.ObjectCount} 个对象 · {FormatBytes(n.TotalSize)}";
        var node = new ChainTreeNode
        {
            Kind = ChainNodeKind.Merged,
            Title = title,
            Loaded = true,
        };
        foreach (MergedChainNode c in n.Children) node.Children.Add(ToTreeNode(c));
        return node;
    }

    /// <summary>即时显示某对象的调用链（保留路径）+ 全部直接引用者。</summary>
    public void ShowChainForAddress(ulong addr)
    {
        if (_index == null) return;
        try
        {
            IReadOnlyList<RetentionPath> paths = _index.GetRetentionPaths(addr, 3);
            IReadOnlyList<ReferenceIndex.ReferrerInfo> refs = _index.GetReferrerInfos(addr);

            ChainPaths.Clear();
            for (int i = 0; i < paths.Count; i++)
                ChainPaths.Add(new ChainPathVm(i + 1, paths[i]));
            ChainReferrers.Clear();
            foreach (var r in refs) ChainReferrers.Add(r);

            string label = _index.GetBrief(addr).TypeName;
            ChainTitle = paths.Count == 0
                ? $"0x{addr:X12}（{label}）：未找到到 GC 根的引用链（对象可能不可达）"
                : $"0x{addr:X12}（{label}）的调用链：{paths.Count} 条保留路径（根类型 {paths[0].RootKind}），另有 {refs.Count} 个直接引用者";
        }
        catch (Exception ex) { ChainTitle = "分析失败: " + ex.Message; }
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

    public void Close() { _analyzer?.Dispose(); _analyzer = null; _index = null; }

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
