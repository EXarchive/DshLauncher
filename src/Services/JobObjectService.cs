using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DshLauncher.Services;

/// <summary>
/// 让子进程"随启动器一起消亡"。
///
/// 解决的问题（已实测复现）：启动器被强杀时（任务管理器结束进程、部署脚本
/// <c>taskkill /IM DshLauncher.exe</c>、崩溃），它拉起的 DSH 实例会变成**孤儿进程**，
/// 继续占用端口与 profile 目录。之后启动器既无法接管它们，重新启动又会被自己的
/// 端口守卫拒绝 —— 这正是本机出现过 3 个孤儿实例的原因。
///
/// 做法：创建一个设置了 <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> 的 Job Object，
/// 把每个由启动器启动的子进程加进去。句柄随进程退出而关闭，Windows 随即结束整个 Job。
///
/// 边界：
///   - 只影响**启动器自己启动**的进程；不由启动器拉起的 DSH 实例不在任何 Job 里。
///   - 加入失败不视为致命错误（例如宿主已把启动器放进禁止嵌套的 Job），只记录一次原因，
///     启动流程继续。
/// </summary>
public static class JobObjectService
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static readonly object Gate = new();
    private static IntPtr _job = IntPtr.Zero;
    private static bool _creationFailed;

    /// <summary>最近一次失败原因（供日志显示），null 表示尚未失败。</summary>
    public static string? LastFailure { get; private set; }

    private static IntPtr EnsureJob()
    {
        lock (Gate)
        {
            if (_job != IntPtr.Zero || _creationFailed) return _job;

            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                _creationFailed = true;
                LastFailure = $"CreateJobObject 失败 (Win32 {Marshal.GetLastWin32Error()})";
                return IntPtr.Zero;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    // 句柄关闭（进程退出）时结束 Job 内全部进程
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
                {
                    LastFailure = $"SetInformationJobObject 失败 (Win32 {Marshal.GetLastWin32Error()})";
                    CloseHandle(job);
                    _creationFailed = true;
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            _job = job;
            return _job;
        }
    }

    /// <summary>
    /// 把刚启动的子进程加入 Job。返回是否成功；失败不抛异常。
    /// </summary>
    public static bool Assign(Process process)
    {
        try
        {
            IntPtr job = EnsureJob();
            if (job == IntPtr.Zero) return false;

            if (!AssignProcessToJobObject(job, process.Handle))
            {
                LastFailure = $"AssignProcessToJobObject 失败 (Win32 {Marshal.GetLastWin32Error()})";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LastFailure = ex.Message;
            return false;
        }
    }
}
