namespace HeapLens.Core;

/// <summary>快照对比引擎：两个快照（dump）的类型级增量分析，用于定位内存泄漏。</summary>
public static class DiffEngine
{
    public static IReadOnlyList<DiffRow> Diff(
        IReadOnlyList<TypeStat> before, IReadOnlyList<TypeStat> after, bool onlyChanged = true)
    {
        var map = new Dictionary<string, DiffRow>(StringComparer.Ordinal);
        foreach (TypeStat t in before)
            map[t.Name] = new DiffRow { Name = t.Name, CountA = t.Count, SizeA = t.TotalSize };
        foreach (TypeStat t in after)
        {
            if (!map.TryGetValue(t.Name, out DiffRow? row))
                row = map[t.Name] = new DiffRow { Name = t.Name };
            row.CountB = t.Count;
            row.SizeB = t.TotalSize;
        }
        var list = map.Values.ToList();
        if (onlyChanged) list.RemoveAll(r => r.DeltaCount == 0 && r.DeltaSize == 0);
        list.Sort((a, b) => Math.Abs(b.DeltaSize).CompareTo(Math.Abs(a.DeltaSize)));
        return list;
    }
}

/// <summary>本机 .NET 进程枚举（供"附加进程"选择用）。</summary>
public static class ProcessScanner
{
    public sealed record DotNetProcess(int Pid, string Name, long MemoryMb, string? Title)
    {
        public string DisplayText => $"[{Pid}] {Name} — {MemoryMb} MB{(string.IsNullOrEmpty(Title) ? "" : $" — {Title}")}";
    }

    public static IReadOnlyList<DotNetProcess> ListDotNetProcesses()
    {
        var result = new List<DotNetProcess>();
        foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                bool isDotNet = false;
                foreach (System.Diagnostics.ProcessModule m in p.Modules)
                {
                    string name = System.IO.Path.GetFileName(m.FileName ?? "");
                    if (name.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("clr.dll", StringComparison.OrdinalIgnoreCase))
                    { isDotNet = true; break; }
                }
                if (!isDotNet) continue;
                result.Add(new DotNetProcess(p.Id, p.ProcessName, p.WorkingSet64 / (1024 * 1024), p.MainWindowTitle));
            }
            catch { /* 跳过无权限访问的进程 */ }
            finally { p.Dispose(); }
        }
        result.Sort((a, b) => b.MemoryMb.CompareTo(a.MemoryMb));
        return result;
    }
}
