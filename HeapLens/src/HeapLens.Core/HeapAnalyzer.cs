using System.Collections.Immutable;
using Microsoft.Diagnostics.Runtime;

namespace HeapLens.Core;

/// <summary>
/// 堆快照分析器：基于 ClrMD（Microsoft.Diagnostics.Runtime）实现。
/// 支持打开 dump 文件 / 快照附加到本机 .NET 进程，并提供类型分布、代龄分布、
/// 堆段/碎片、GC 根、保留路径等分析能力。
/// </summary>
public sealed class HeapAnalyzer : IDisposable
{
    private readonly DataTarget _dataTarget;
    private readonly ClrRuntime _runtime;

    public ClrHeap Heap { get; }
    public string Source { get; }
    public string RuntimeVersion { get; }

    private HeapAnalyzer(DataTarget dt, ClrRuntime rt, string source)
    {
        _dataTarget = dt;
        _runtime = rt;
        Heap = rt.Heap;
        Source = source;
        ClrInfo info = dt.ClrVersions[0];
        RuntimeVersion = $"{info.Flavor} {info.Version}";
    }

    /// <summary>打开 dump 文件（.dmp / 蓝屏 dump 等）。</summary>
    public static HeapAnalyzer OpenDump(string path)
    {
        DataTarget dt = DataTarget.LoadDump(path, null);
        return FromDataTarget(dt, path);
    }

    /// <summary>对本机进程创建内存快照并分析（不干扰目标进程运行）。</summary>
    public static HeapAnalyzer AttachProcess(int pid)
    {
        DataTarget dt = DataTarget.CreateSnapshotAndAttach(pid);
        return FromDataTarget(dt, $"pid:{pid}");
    }

    private static HeapAnalyzer FromDataTarget(DataTarget dt, string source)
    {
        if (dt.ClrVersions.IsDefaultOrEmpty)
            throw new InvalidOperationException("未在目标中发现 .NET 运行时（不是 .NET 进程或 dump 不含 CLR 数据）。");
        ClrRuntime rt = dt.ClrVersions[0].CreateRuntime();
        return new HeapAnalyzer(dt, rt, source);
    }

    // ---------- 总览 ----------

    public HeapOverview GetOverview()
    {
        var ov = new HeapOverview { IsServerGc = Heap.IsServer, RuntimeVersion = RuntimeVersion };
        foreach (ClrSegment seg in Heap.Segments)
        {
            ov.SegmentCount++;
            ov.CommittedSize += (long)seg.CommittedMemory.Length;
            ov.ReservedSize += (long)seg.ReservedMemory.Length;
        }
        foreach (var (gen, size) in EnumerateGenerations())
        {
            ov.TotalObjectSize += size;
            switch (gen)
            {
                case GenBucket.Gen0: ov.Gen0Size += size; break;
                case GenBucket.Gen1: ov.Gen1Size += size; break;
                case GenBucket.Gen2: ov.Gen2Size += size; break;
                case GenBucket.Loh: ov.LohSize += size; break;
                case GenBucket.Poh: ov.PohSize += size; break;
                case GenBucket.Free: ov.FreeSize += size; break;
            }
            if (gen != GenBucket.Free) ov.ObjectCount++;
        }
        return ov;
    }

    /// <summary>按类型聚合的分布统计。</summary>
    public IReadOnlyList<TypeStat> GetTypeStats()
    {
        var map = new Dictionary<string, TypeStat>(StringComparer.Ordinal);
        foreach (ClrObject obj in Heap.EnumerateObjects())
        {
            if (obj.IsFree) continue;
            TypeStat stat = GetOrAdd(map, obj);
            long size = (long)obj.Size;
            stat.Count++;
            stat.TotalSize += size;
            if (size > stat.MaxSize) stat.MaxSize = size;
            switch (GetBucket(obj))
            {
                case GenBucket.Gen0: stat.Gen0Count++; break;
                case GenBucket.Gen1: stat.Gen1Count++; break;
                case GenBucket.Gen2: stat.Gen2Count++; break;
                case GenBucket.Loh: stat.LohCount++; break;
                case GenBucket.Poh: stat.PohCount++; break;
            }
        }
        var list = map.Values.ToList();
        list.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));
        return list;
    }

    /// <summary>某类型的实例列表（按大小降序）。</summary>
    public IReadOnlyList<InstanceInfo> GetInstances(string typeName, int limit)
    {
        var list = new List<InstanceInfo>();
        foreach (ClrObject obj in Heap.EnumerateObjects())
        {
            if (obj.IsFree) continue;
            if (GetTypeName(obj) != typeName) continue;
            list.Add(new InstanceInfo
            {
                Address = obj.Address,
                Size = (long)obj.Size,
                Preview = TryPreview(obj),
            });
            if (list.Count >= limit * 8) break; // 收集一批后排序截断
        }
        list.Sort((a, b) => b.Size.CompareTo(a.Size));
        return list.Count > limit ? list.GetRange(0, limit) : list;
    }

    /// <summary>堆段与碎片信息。</summary>
    public IReadOnlyList<SegmentStat> GetSegments()
    {
        var list = new List<SegmentStat>();
        foreach (ClrSegment seg in Heap.Segments)
        {
            var s = new SegmentStat
            {
                Start = seg.Start,
                End = seg.End,
                Length = seg.Length,
                Committed = seg.CommittedMemory.Length,
                Reserved = seg.ReservedMemory.Length,
                Gen0Size = (long)seg.Generation0.Length,
                Gen1Size = (long)seg.Generation1.Length,
                Gen2Size = (long)seg.Generation2.Length,
                IsEphemeral = seg.IsEphemeralSegment,
                IsLargeObject = seg.IsLargeObjectSegment,
                IsPinned = seg.IsPinnedObjectSegment,
                LogicalHeap = seg.LogicalHeap,
            };
            list.Add(s);
        }
        return list;
    }

    /// <summary>GC 根列表。</summary>
    public IReadOnlyList<RootInfo> GetRoots(int limit)
    {
        var list = new List<RootInfo>();
        foreach (IClrRoot root in Heap.EnumerateRoots())
        {
            list.Add(new RootInfo
            {
                Address = root.Object.Address,
                Kind = root.RootKind.ToString(),
                TypeName = GetTypeName(root.Object),
            });
            if (list.Count >= limit) break;
        }
        return list;
    }

    /// <summary>
    /// 字符串搜索（对标 dotMemory Strings 视图）：按字符串内容对 System.String 实例分组，
    /// keyword 非空时仅保留包含该关键字（忽略大小写）的内容；按组总大小降序返回前 maxGroups 组。
    /// </summary>
    public StringSearchResult SearchStrings(string keyword, int maxGroups = 500, int maxScan = 1_000_000)
    {
        var result = new StringSearchResult();
        var map = new Dictionary<string, StringGroup>(StringComparer.Ordinal);
        foreach (ClrObject obj in Heap.EnumerateObjects())
        {
            if (obj.IsFree) continue;
            if (GetTypeName(obj) != "System.String") continue;
            if (++result.ScannedCount > maxScan) { result.Truncated = true; break; }
            string s;
            try { s = obj.AsString(1024) ?? ""; }
            catch { continue; }
            if (keyword.Length > 0 && !s.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
            result.MatchedCount++;
            if (!map.TryGetValue(s, out StringGroup? g))
                map[s] = g = new StringGroup { Value = s.Length > 160 ? s[..160] + "…" : s, Count = 0, TotalSize = 0 };
            g.Count++;
            g.TotalSize += (long)obj.Size;
        }
        result.Groups.AddRange(map.Values);
        result.Groups.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));
        if (result.Groups.Count > maxGroups) result.Groups.RemoveRange(maxGroups, result.Groups.Count - maxGroups);
        return result;
    }

    /// <summary>保留路径分析：目标对象为什么不能被 GC 回收（经哪条引用链挂在 GC 根上）。</summary>
    public RetentionPath? GetRetentionPath(ulong targetAddress, int timeoutMs = 120_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var gcRoot = new GCRoot(Heap);

        // 先尝试精确模式（查找所有根路径），失败再退回单路径模式
        foreach (bool searchMultiple in new[] { true, false })
        {
            foreach (GCRootPath rootPath in gcRoot.EnumerateGCRoots(targetAddress, searchMultiple, cts.Token))
            {
                var path = new RetentionPath { RootKind = rootPath.Root.RootKind.ToString() };
                ImmutableArray<ClrObject> chain = rootPath.Path;
                for (int i = 0; i < chain.Length; i++)
                {
                    ClrObject obj = chain[i];
                    string? field = i == 0 ? null : FindFieldName(chain[i - 1], obj);
                    path.Nodes.Add(new RetentionNode
                    {
                        Address = obj.Address,
                        TypeName = GetTypeName(obj),
                        FieldName = field,
                        Size = (long)obj.Size,
                        Preview = TryPreview(obj),
                    });
                }
                return path;
            }
            if (cts.IsCancellationRequested) break;
        }
        return null;
    }

    // ---------- 内部实现 ----------

    private enum GenBucket { Gen0, Gen1, Gen2, Loh, Poh, Free }

    private IEnumerable<(GenBucket, long)> EnumerateGenerations()
    {
        foreach (ClrObject obj in Heap.EnumerateObjects())
        {
            if (obj.IsFree) yield return (GenBucket.Free, (long)obj.Size);
            else yield return (GetBucket(obj), (long)obj.Size);
        }
    }

    private static GenBucket GetBucket(ClrObject obj)
    {
        ClrSegment? seg = obj.Type?.Heap.GetSegmentByAddress(obj.Address);
        if (seg == null) return GenBucket.Gen2;
        if (seg.IsLargeObjectSegment) return GenBucket.Loh;
        if (seg.IsPinnedObjectSegment) return GenBucket.Poh;
        return seg.GetGeneration(obj.Address) switch
        {
            0 => GenBucket.Gen0,
            1 => GenBucket.Gen1,
            _ => GenBucket.Gen2,
        };
    }

    private static TypeStat GetOrAdd(Dictionary<string, TypeStat> map, ClrObject obj)
    {
        string name = GetTypeName(obj);
        if (!map.TryGetValue(name, out TypeStat? stat))
        {
            stat = new TypeStat { Name = name, MethodTable = obj.Type?.MethodTable ?? 0 };
            map[name] = stat;
        }
        return stat;
    }

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

    /// <summary>在父对象中找出指向子对象的字段/数组元素名。</summary>
    private static string? FindFieldName(ClrObject parent, ClrObject child)
    {
        try
        {
            foreach (ClrReference r in parent.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: false))
            {
                if (r.Object.Address != child.Address) continue;
                if (r.IsArrayElement) return "[数组元素]";
                return r.Field?.Name;
            }
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        _runtime.Dispose();
        _dataTarget.Dispose();
    }
}
