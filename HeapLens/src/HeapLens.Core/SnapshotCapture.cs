using System.Runtime.InteropServices;

namespace HeapLens.Core;

/// <summary>
/// 进程快照捕获：用 dbghelp!MiniDumpWriteDump 把目标 .NET 进程的堆内存
/// 导出为 dump 文件（即"快照"），随后可用 <see cref="HeapAnalyzer.OpenDump"/> 分析。
/// </summary>
public static class SnapshotCapture
{
    [Flags]
    private enum MiniDumpType : uint
    {
        WithFullMemory = 0x00000002,
        WithHandleData = 0x00000004,
        WithUnloadedModules = 0x00000020,
        WithThreadInfo = 0x00001000,
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess, uint processId, IntPtr hFile,
        MiniDumpType dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    /// <summary>捕获指定进程的内存快照到 dump 文件。</summary>
    public static void Capture(int pid, string dumpPath)
    {
        IntPtr hProcess = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        if (hProcess == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"无法打开进程 {pid}（权限不足？）");

        using var fs = new FileStream(dumpPath, FileMode.Create, FileAccess.ReadWrite);
        try
        {
            var flags = MiniDumpType.WithFullMemory
                      | MiniDumpType.WithHandleData
                      | MiniDumpType.WithThreadInfo
                      | MiniDumpType.WithUnloadedModules;
            if (!MiniDumpWriteDump(hProcess, (uint)pid, fs.SafeFileHandle.DangerousGetHandle(), flags,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "MiniDumpWriteDump 失败");
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }
}
