namespace HeapLens.Core;

/// <summary>单个托管类型的堆统计。</summary>
public sealed class TypeStat
{
    public string Name { get; set; } = "";
    public ulong MethodTable { get; set; }
    public long Count { get; set; }
    public long TotalSize { get; set; }
    public long Gen0Count { get; set; }
    public long Gen1Count { get; set; }
    public long Gen2Count { get; set; }
    public long LohCount { get; set; }
    public long PohCount { get; set; }
    public long MaxSize { get; set; }
    public double AvgSize => Count == 0 ? 0 : (double)TotalSize / Count;
}

/// <summary>堆段（segment）信息。</summary>
public sealed class SegmentStat
{
    public ulong Start { get; set; }
    public ulong End { get; set; }
    public ulong Length { get; set; }
    public ulong Committed { get; set; }
    public ulong Reserved { get; set; }
    public long Gen0Size { get; set; }
    public long Gen1Size { get; set; }
    public long Gen2Size { get; set; }
    public bool IsEphemeral { get; set; }
    public bool IsLargeObject { get; set; }
    public bool IsPinned { get; set; }
    public int LogicalHeap { get; set; }
    public string SegType => IsLargeObject ? "LOH" : IsPinned ? "POH" : IsEphemeral ? "Ephemeral" : "Gen2";
}

/// <summary>堆总览。</summary>
public sealed class HeapOverview
{
    public long TotalObjectSize { get; set; }
    public long CommittedSize { get; set; }
    public long ReservedSize { get; set; }
    public long ObjectCount { get; set; }
    public long FreeSize { get; set; }
    public long Gen0Size { get; set; }
    public long Gen1Size { get; set; }
    public long Gen2Size { get; set; }
    public long LohSize { get; set; }
    public long PohSize { get; set; }
    public int SegmentCount { get; set; }
    public bool IsServerGc { get; set; }
    public string RuntimeVersion { get; set; } = "";
}

/// <summary>GC 根。</summary>
public sealed class RootInfo
{
    public ulong Address { get; set; }
    public string Kind { get; set; } = "";
    public string TypeName { get; set; } = "";
}

/// <summary>保留路径上的一个节点（从 GC 根到目标对象）。</summary>
public sealed class RetentionNode
{
    public ulong Address { get; set; }
    public string TypeName { get; set; } = "";
    public string? FieldName { get; set; }
    public long Size { get; set; }
    public string? Preview { get; set; }
}

/// <summary>一条完整保留路径。</summary>
public sealed class RetentionPath
{
    public List<RetentionNode> Nodes { get; } = new();
    public string RootKind { get; set; } = "";
}

/// <summary>单个对象条目（实例列表用）。</summary>
public sealed class InstanceInfo
{
    public ulong Address { get; set; }
    public long Size { get; set; }
    public string? Preview { get; set; }
}

/// <summary>快照 diff 行。</summary>
public sealed class DiffRow
{
    public string Name { get; set; } = "";
    public long CountA { get; set; }
    public long CountB { get; set; }
    public long SizeA { get; set; }
    public long SizeB { get; set; }
    public long DeltaCount => CountB - CountA;
    public long DeltaSize => SizeB - SizeA;
}
