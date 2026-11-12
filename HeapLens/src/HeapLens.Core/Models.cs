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
    /// <summary>保留大小合计（支配树 Retained Size，对标 dotMemory Retained bytes）。</summary>
    public long RetainedSize { get; set; }
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

/// <summary>
/// 合并持有链树节点（对标 dotMemory 的 merged retention paths）：
/// 同一层级上（类型名, 字段）相同的持有者聚合为一个节点。
/// </summary>
public sealed class MergedChainNode
{
    /// <summary>持有者类型名（叶子层为目标类型）。</summary>
    public string TypeName { get; set; } = "";
    /// <summary>经由哪个字段持有下一层（叶子为空）。</summary>
    public string? Field { get; set; }
    /// <summary>经此节点持有到的目标实例数。</summary>
    public int TargetCount { get; set; }
    /// <summary>该层级聚合的去重对象数。</summary>
    public int ObjectCount { get; set; }
    /// <summary>该层级聚合对象的浅大小合计。</summary>
    public long TotalSize { get; set; }
    public List<MergedChainNode> Children { get; } = new();
}

/// <summary>类型级保留大小（支配树 retained size，对标 dotMemory Retained bytes）。</summary>
public sealed class RetainedInfo
{
    public string Name { get; set; } = "";
    public long RetainedSize { get; set; }
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
