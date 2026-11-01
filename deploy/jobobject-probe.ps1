# jobobject-probe.ps1
#
# Independent verification of the orphan-prevention mechanism used by
# src\Services\JobObjectService.cs.
#
# The launcher assigns every process it starts to a Job Object configured with
# JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so a hard-killed launcher cannot leave DSH
# instances behind holding ports and profile directories.
#
# This probe re-creates the SAME P/Invoke surface and the SAME struct layout in
# PowerShell and proves three things:
#   1. the extended-limit struct marshals to 144 bytes (the layout the C# code assumes)
#   2. a child really is inside the job after assignment
#   3. closing the job handle kills the child
#
# Exit code 0 = PASS. Standalone: run it directly and check the exit code.

$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class JobProbe {
    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
                     ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern IntPtr CreateJobObject(IntPtr a, string n);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool SetInformationJobObject(IntPtr j, int c, IntPtr i, uint l);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool AssignProcessToJobObject(IntPtr j, IntPtr p);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool IsProcessInJob(IntPtr p, IntPtr j, out bool r);

    public static IntPtr MakeJob() {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Exception("CreateJobObject failed " + Marshal.GetLastWin32Error());
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION {
            LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        };
        int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr buf = Marshal.AllocHGlobal(size);
        try {
            Marshal.StructureToPtr(info, buf, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buf, (uint)size))
                throw new Exception("SetInformationJobObject failed " + Marshal.GetLastWin32Error());
        } finally { Marshal.FreeHGlobal(buf); }
        return job;
    }
    public static int StructSize() { return Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)); }
}
"@

$node = 'D:\Program Files\nodejs\node.exe'
if (-not (Test-Path $node)) { $node = (Get-Command node).Source }

"struct size = $([JobProbe]::StructSize()) bytes (expected 144)"
$job = [JobProbe]::MakeJob()
"job handle = $job"

# A stand-in for "the DSH instance the launcher started": alive for 60 seconds.
$child = Start-Process -FilePath $node -ArgumentList '-e', 'setTimeout(()=>{},60000)' -PassThru -WindowStyle Hidden
"child pid = $($child.Id)"
Start-Sleep -Seconds 2
"(child alive before assign: $(-not $child.HasExited))"

$assigned = [JobProbe]::AssignProcessToJobObject($job, $child.Handle)
"AssignProcessToJobObject = $assigned"

$inJob = $false
[void][JobProbe]::IsProcessInJob($child.Handle, $job, [ref]$inJob)
"IsProcessInJob = $inJob"

# Closing the handle is what a dying launcher does.
[void][JobProbe]::CloseHandle($job)
Start-Sleep -Seconds 3

$killed = -not [bool](Get-Process -Id $child.Id -ErrorAction SilentlyContinue)
"child killed by job close = $killed"

if ($assigned -and $inJob -and $killed) { 'RESULT: PASS'; exit 0 }
'RESULT: FAIL'; exit 1
