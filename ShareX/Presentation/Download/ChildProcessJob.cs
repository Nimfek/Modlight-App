#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShareX;

/// <summary>
/// A Windows job object that kills everything inside it when Modlight goes away.
/// </summary>
/// <remarks>
/// A download is <c>yt-dlp.exe</c>, and a merging download is <c>yt-dlp.exe</c> with an
/// <c>ffmpeg.exe</c> underneath it. Killing them on cancel is easy - Process.Kill takes
/// the tree. The case that needs this is the one nothing in managed code can cover:
/// Modlight itself being killed, crashing, or being ended from Task Manager. There is no
/// callback for that. The processes would simply keep running, keep writing into somebody's
/// Downloads folder and keep holding the connection, with nothing on screen that owns them.
///
/// A job object with KILL_ON_JOB_CLOSE is the one mechanism that survives it: the handle is
/// closed by the kernel when the last process holding it dies, however it dies, and closing
/// it terminates every process assigned to the job. So the children cannot outlive us even
/// when we are given no chance to tidy up.
///
/// Everything here is best-effort. If the job cannot be created - a sandbox with the API
/// denied, an old nested-job restriction - downloads still work; they just lose the
/// guarantee, which is a worse day than usual rather than a broken feature. Nested jobs
/// have been allowed since Windows 8 and this app requires Windows 10.
/// </remarks>
internal static class ChildProcessJob
{
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    private static readonly object gate = new();

    private static IntPtr handle = IntPtr.Zero;
    private static bool tried;

    /// <summary>
    /// Put a freshly started process in the job, so it cannot outlive this one.
    /// </summary>
    /// <remarks>
    /// Called immediately after Start. There is a window of a few milliseconds in which a
    /// grandchild started before this call would escape the job - in practice ffmpeg is
    /// only spawned once the download has finished, seconds or minutes later, so the
    /// window is empty. Closing it properly would mean starting the process suspended and
    /// resuming it by hand, which is a lot of native code to buy nothing measurable.
    /// </remarks>
    public static void Adopt(Process process)
    {
        IntPtr job = Ensure();

        if (job == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                DebugHelper.WriteLine("Download: could not put yt-dlp in the job object (error " +
                    Marshal.GetLastWin32Error() + "). It will still be killed on cancel.");
            }
        }
        catch (Exception exception)
        {
            // Process.Handle throws once the process has already exited, which is a real
            // possibility for something that fails on its first line of output.
            DebugHelper.WriteException(exception);
        }
    }

    private static IntPtr Ensure()
    {
        lock (gate)
        {
            if (tried)
            {
                return handle;
            }

            tried = true;

            try
            {
                IntPtr created = CreateJobObject(IntPtr.Zero, null);

                if (created == IntPtr.Zero)
                {
                    DebugHelper.WriteLine("Download: no job object (error " + Marshal.GetLastWin32Error() + ").");
                    return IntPtr.Zero;
                }

                JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new()
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };

                int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr buffer = Marshal.AllocHGlobal(size);

                try
                {
                    Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);

                    if (!SetInformationJobObject(created, JobObjectExtendedLimitInformation, buffer, (uint)size))
                    {
                        // A job without the flag would keep the children alive after a
                        // crash, which is the entire point of having one. Better to have
                        // no job at all than one that quietly does not do its job.
                        DebugHelper.WriteLine("Download: job object would not take the kill-on-close limit (error " +
                            Marshal.GetLastWin32Error() + ").");
                        CloseHandle(created);
                        return IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                handle = created;
            }
            catch (Exception exception)
            {
                DebugHelper.WriteException(exception);
            }

            return handle;
        }
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
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
