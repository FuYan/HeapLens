// HeapLens.SmokeTest —— 命令行验证工具
// 用法:
//   SmokeTest capture <pid> <dump文件>            捕获进程快照
//   SmokeTest analyze <dump文件> [--top N]         分析 dump：总览 + 类型分布
//   SmokeTest path <dump文件> <对象地址(hex)>      保留路径分析（GCRoot 搜索）
//   SmokeTest index <dump文件>                     构建引用索引并输出统计（含耗时）
//   SmokeTest chains <dump文件> <类型> [N]          查看某类型前 N 个实例的调用链（保留路径）
//   SmokeTest refs <dump文件> <对象地址(hex)>       查看对象的直接引用者 / 被引用对象
//   SmokeTest diff <dumpA> <dumpB>                 快照对比
using System.Diagnostics;
using HeapLens.Core;

if (args.Length == 0)
{
    Console.WriteLine("用法: SmokeTest capture <pid> <dump> | analyze <dump> [--top N] | instances <dump> <类型> [N] | path <dump> <addr> | index <dump> | chains <dump> <类型> [N] | refs <dump> <addr> | diff <a> <b>");
    return 0;
}

switch (args[0])
{
    case "capture":
    {
        int pid = int.Parse(args[1]);
        SnapshotCapture.Capture(pid, args[2]);
        Console.WriteLine($"已捕获 pid={pid} 的快照 -> {args[2]}");
        return 0;
    }
    case "analyze":
    {
        int top = args.Length > 2 && args[2] == "--top" ? int.Parse(args[3]) : 20;
        using var an = HeapAnalyzer.OpenDump(args[1]);
        HeapOverview ov = an.GetOverview();
        Console.WriteLine($"来源: {an.Source}   运行时: {an.RuntimeVersion}   GC: {(ov.IsServerGc ? "Server" : "Workstation")}");
        Console.WriteLine($"对象数: {ov.ObjectCount:N0}   存活对象总大小: {FormatBytes(ov.TotalObjectSize)}");
        Console.WriteLine($"Gen0: {FormatBytes(ov.Gen0Size)}  Gen1: {FormatBytes(ov.Gen1Size)}  Gen2: {FormatBytes(ov.Gen2Size)}  LOH: {FormatBytes(ov.LohSize)}  POH: {FormatBytes(ov.PohSize)}");
        Console.WriteLine($"已提交: {FormatBytes(ov.CommittedSize)}  保留: {FormatBytes(ov.ReservedSize)}  Free(碎片): {FormatBytes(ov.FreeSize)}  段数: {ov.SegmentCount}");
        Console.WriteLine();
        Console.WriteLine($"{"类型",-60} {"数量",12} {"总大小",14} {"平均",10} {"Gen2",8} {"LOH",8}");
        foreach (TypeStat t in an.GetTypeStats().Take(top))
            Console.WriteLine($"{Trunc(t.Name, 60),-60} {t.Count,12:N0} {FormatBytes(t.TotalSize),14} {FormatBytes((long)t.AvgSize),10} {t.Gen2Count,8:N0} {t.LohCount,8:N0}");
        return 0;
    }
    case "path":
    {
        ulong addr = ulong.Parse(args[2], System.Globalization.NumberStyles.HexNumber);
        using var an = HeapAnalyzer.OpenDump(args[1]);
        Console.WriteLine($"分析 0x{addr:X} 的保留路径（可能需要一两分钟）...");
        RetentionPath? p = an.GetRetentionPath(addr);
        if (p == null) { Console.WriteLine("未找到保留路径（对象可能已死或不在堆上）。"); return 1; }
        Console.WriteLine($"GC 根类型: {p.RootKind}");
        foreach (RetentionNode n in p.Nodes)
        {
            string field = n.FieldName != null ? $".{n.FieldName}" : "";
            string preview = n.Preview != null ? $"  \"{n.Preview}\"" : "";
            Console.WriteLine($"  0x{n.Address:X12}  {Trunc(n.TypeName + field, 80)}  ({FormatBytes(n.Size)}){preview}");
        }
        return 0;
    }
    case "index":
    {
        using var an = HeapAnalyzer.OpenDump(args[1]);
        var sw = Stopwatch.StartNew();
        ReferenceIndex idx = ReferenceIndex.Build(an.Heap, n => Console.Write($"\r已处理 {n:N0} 个对象…"));
        sw.Stop();
        Console.WriteLine($"\r引用索引构建完成：{idx.ObjectCount:N0} 个对象，{idx.EdgeCount:N0} 条引用边，{idx.RootCount:N0} 个 GC 根，耗时 {sw.Elapsed.TotalSeconds:0.0}s");
        foreach (ReferenceIndex.RootGroupInfo g in idx.GetRootGroups(10))
            Console.WriteLine($"  根分组 {g.Kind,-16} {g.TotalCount,10:N0} 个");
        return 0;
    }
    case "chains":
    {
        int limit = args.Length > 3 ? int.Parse(args[3]) : 5;
        using var an = HeapAnalyzer.OpenDump(args[1]);
        Console.WriteLine("构建引用索引…");
        ReferenceIndex idx = ReferenceIndex.Build(an.Heap);
        IReadOnlyList<InstanceInfo> insts = idx.GetInstances(args[2], limit);
        if (insts.Count == 0) { Console.WriteLine($"未找到类型 {args[2]} 的实例"); return 1; }
        foreach (InstanceInfo inst in insts)
        {
            Console.WriteLine($"\n=== 实例 0x{inst.Address:X12}  {FormatBytes(inst.Size)}  {inst.Preview ?? ""}");
            IReadOnlyList<RetentionPath> paths = idx.GetRetentionPaths(inst.Address, 3);
            if (paths.Count == 0) { Console.WriteLine("  （未找到到 GC 根的引用链）"); continue; }
            for (int pi = 0; pi < paths.Count; pi++)
            {
                RetentionPath p = paths[pi];
                Console.WriteLine($"  -- 路径 {pi + 1}（根类型 {p.RootKind}，共 {p.Nodes.Count} 层）:");
                foreach (RetentionNode n in p.Nodes)
                {
                    string field = n.FieldName != null ? $".{n.FieldName}" : "";
                    string preview = n.Preview != null ? $"  \"{n.Preview}\"" : "";
                    Console.WriteLine($"     0x{n.Address:X12}  {Trunc(n.TypeName + field, 80)}  ({FormatBytes(n.Size)}){preview}");
                }
            }
        }
        return 0;
    }
    case "refs":
    {
        ulong addr = ulong.Parse(args[2], System.Globalization.NumberStyles.HexNumber);
        using var an = HeapAnalyzer.OpenDump(args[1]);
        ReferenceIndex idx = ReferenceIndex.Build(an.Heap);
        Console.WriteLine($"0x{addr:X12} 的直接引用者（调用链上一层）:");
        foreach (ReferenceIndex.ReferrerInfo r in idx.GetReferrerInfos(addr))
            Console.WriteLine($"  0x{r.Parent:X12}  .{(r.Field ?? "-"),-24}  {Trunc(r.TypeName, 60)}  ({FormatBytes(r.Size)})");
        Console.WriteLine($"0x{addr:X12} 的直接被引用对象（调用链下一层）:");
        foreach (ReferenceIndex.ChildInfo c in idx.GetChildren(addr, 50))
            Console.WriteLine($"  0x{c.Address:X12}  .{(c.Field ?? "-"),-24}  {Trunc(c.TypeName, 60)}  ({FormatBytes(c.Size)})");
        return 0;
    }
    case "instances":
    {
        int limit = args.Length > 3 ? int.Parse(args[3]) : 10;
        using var an = HeapAnalyzer.OpenDump(args[1]);
        foreach (InstanceInfo inst in an.GetInstances(args[2], limit))
            Console.WriteLine($"  0x{inst.Address:X12}  {FormatBytes(inst.Size),10}  {inst.Preview ?? ""}");
        return 0;
    }
    case "diff":
    {
        using var a = HeapAnalyzer.OpenDump(args[1]);
        using var b = HeapAnalyzer.OpenDump(args[2]);
        IReadOnlyList<DiffRow> rows = DiffEngine.Diff(a.GetTypeStats(), b.GetTypeStats());
        Console.WriteLine($"{"类型",-55} {"数量A",10} {"数量B",10} {"Δ数量",10} {"Δ大小",14}");
        foreach (DiffRow r in rows.Take(25))
            Console.WriteLine($"{Trunc(r.Name, 55),-55} {r.CountA,10:N0} {r.CountB,10:N0} {r.DeltaCount,10:N0} {FormatBytes(r.DeltaSize),14}");
        return 0;
    }
    default:
        Console.WriteLine($"未知命令: {args[0]}");
        return 1;
}

static string FormatBytes(long b)
{
    string[] units = { "B", "KB", "MB", "GB" };
    double v = Math.Abs(b);
    int u = 0;
    while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
    return $"{(b < 0 ? "-" : "")}{v:0.##} {units[u]}";
}

static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
