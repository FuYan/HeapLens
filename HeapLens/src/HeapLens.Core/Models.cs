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
    /// <summary>是否为命名空间聚合行（命名空间分组视图用）。</summary>
    public bool IsNamespaceGroup { get; set; }
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
    /// <summary>该节点对象的保留大小（支配树 Retained，单对象 Retained 显示）。</summary>
    public long RetainedSize { get; set; }
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
    /// <summary>该实例的保留大小（支配树 Retained）。</summary>
    public long RetainedSize { get; set; }
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
    /// <summary>被父节点经该字段持有（与 RetentionNode.FieldName 语义一致；根下第一层为空）。</summary>
    public string? Field { get; set; }
    /// <summary>经此节点持有到的目标实例数。</summary>
    public int TargetCount { get; set; }
    /// <summary>该层级聚合的去重对象数。</summary>
    public int ObjectCount { get; set; }
    /// <summary>该层级聚合对象的浅大小合计。</summary>
    public long TotalSize { get; set; }
    /// <summary>该层级聚合对象的保留大小合计（支配树 Retained，对标 dotMemory 合并树节点 Retained）。</summary>
    public long RetainedSize { get; set; }
    public List<MergedChainNode> Children { get; } = new();
}

/// <summary>类型级保留大小（支配树 retained size，对标 dotMemory Retained bytes）。</summary>
public sealed class RetainedInfo
{
    public string Name { get; set; } = "";
    public long RetainedSize { get; set; }
}

/// <summary>支配对象行（Dominators 视图：Retained Size 最大的对象）。</summary>
public sealed class DominatorRow
{
    public ulong Address { get; set; }
    public string TypeName { get; set; } = "";
    public long Size { get; set; }
    public long RetainedSize { get; set; }
}

/// <summary>字符串搜索结果组（按字符串内容分组，对标 dotMemory Strings 视图）。</summary>
public sealed class StringGroup
{
    /// <summary>字符串内容（超长截断至 160 字符显示）。</summary>
    public string Value { get; set; } = "";
    /// <summary>相同内容的实例数。</summary>
    public int Count { get; set; }
    /// <summary>该组实例总大小（浅大小合计）。</summary>
    public long TotalSize { get; set; }
}

/// <summary>字符串搜索汇总结果。</summary>
public sealed class StringSearchResult
{
    public List<StringGroup> Groups { get; } = new();
    /// <summary>匹配到的字符串实例总数（去重前）。</summary>
    public int MatchedCount { get; set; }
    /// <summary>扫描的字符串实例总数。</summary>
    public int ScannedCount { get; set; }
    /// <summary>是否因扫描上限截断。</summary>
    public bool Truncated { get; set; }
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
