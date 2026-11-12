// LeakyApp：测试用的 .NET 进程，故意保留一批对象模拟内存泄漏，
// 供 HeapLens 捕获快照并验证类型分布 / 保留路径 / diff 分析。
var leak = new List<byte[]>();
var cache = new Dictionary<string, Holder>();

for (int i = 0; i < 3000; i++)
{
    leak.Add(new byte[10_000 + (i % 100)]);
    cache["key-" + i] = new Holder(
        new Payload("消息编号-" + i, new string('x', 200), DateTime.Now));
}

Console.WriteLine($"LeakyApp 启动完成，持有 {leak.Count} 个 byte[] 和 {cache.Count} 个 Holder。");
Console.WriteLine("PID=" + Environment.ProcessId);
Console.Out.Flush();

// 每秒继续泄漏 200 个 byte[]，模拟持续增长（供快照 diff 测试）
int n = 0;
while (true)
{
    for (int i = 0; i < 200; i++) leak.Add(new byte[10_000]);
    await Task.Delay(1000);
    if (++n % 10 == 0) Console.WriteLine($"已持续泄漏 {n} 秒，byte[] 总数 ≈ {leak.Count}");
}

internal sealed record Payload(string Title, string Body, DateTime Created);
internal sealed record Holder(Payload Payload);
