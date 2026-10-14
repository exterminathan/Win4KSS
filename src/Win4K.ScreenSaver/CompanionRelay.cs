using System.Diagnostics;
using System.Runtime.InteropServices;
using Win4K.Core;

namespace Win4K.ScreenSaver;

/// <summary>
/// The one-file .scr in System32 does no work of its own: it runs the user-scope install's
/// Win4KSS.exe with the same arguments and waits for it. A per-application firewall that
/// allowed the installed exe has never seen the System32 copy, and on a fresh PC that blocked
/// every stream the saver tried — Windows always launches /s from System32 — so it sat black.
/// Relaying keeps every network call, decode and firewall decision on one executable.
/// </summary>
internal static partial class CompanionRelay
{
    public const string ExeName = "Win4KSS.exe";

    /// <summary>
    /// The exe to relay to, or null to run in this process: natives beside us (the install
    /// itself, or a dev build), we already are the companion dir, or there is no install to relay to.
    /// </summary>
    internal static string? Target(string exeDirectory, string companionDirectory, Func<string, bool> fileExists)
    {
        if (fileExists(Path.Combine(exeDirectory, NativeDependencies.LibMpv)))
            return null;

        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(exeDirectory)),
                          Path.TrimEndingDirectorySeparator(Path.GetFullPath(companionDirectory)),
                          StringComparison.OrdinalIgnoreCase))
            return null;

        var target = Path.Combine(companionDirectory, ExeName);
        return fileExists(target) ? target : null;
    }

    /// <summary>
    /// The arguments to hand on. No arguments means Configure for a .scr, but Window for an
    /// .exe (see Program.ParseArguments), so that case is spelled out.
    /// </summary>
    internal static IReadOnlyList<string> RelayedArguments(string[] args) => args.Length == 0 ? ["/c"] : args;

    /// <returns>True if the companion ran; <paramref name="exitCode"/> is then its exit code.</returns>
    public static bool TryRelay(string[] args, out int exitCode)
    {
        exitCode = 0;

        string? target;
        try
        {
            target = Target(AppContext.BaseDirectory, AppPaths.CompanionDirectory, File.Exists);
        }
        catch (Exception ex)
        {
            Log.Warn($"Relay: could not check for the installed copy ({ex.Message}); running here");
            return false;
        }

        if (target is null)
            return false;

        var start = new ProcessStartInfo(target)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(target)!,
        };
        foreach (var arg in RelayedArguments(args))
            start.ArgumentList.Add(arg);

        Process? child;
        try
        {
            child = Process.Start(start);
        }
        catch (Exception ex)
        {
            Log.Error($"Relay: could not start {target}; running here instead", ex);
            return false;
        }

        if (child is null)
            return false;

        using (child)
        {
            Log.Info($"Relay: {Environment.ProcessPath} -> {target} {string.Join(' ', start.ArgumentList)} (pid {child.Id})");

            // If Windows ends this process, the saver it started must not outlive it.
            using var job = KillOnCloseJob.Create(child);

            child.WaitForExit();
            exitCode = child.ExitCode;
            return true;
        }
    }

    /// <summary>
    /// A job that kills its one process when the last handle closes. Silent breakaway lets that
    /// process's own children (the update installer the browser launches) outlive it.
    /// </summary>
    private sealed partial class KillOnCloseJob : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x1000;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        private IntPtr _handle;

        private KillOnCloseJob(IntPtr handle) => _handle = handle;

        public static KillOnCloseJob? Create(Process process)
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                Log.Warn($"Relay: CreateJobObject failed, error {Marshal.GetLastPInvokeError()}");
                return null;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK,
                },
            };

            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()) ||
                !AssignProcessToJobObject(job, process.Handle))
            {
                Log.Warn($"Relay: could not tie the saver to this process, error {Marshal.GetLastPInvokeError()}");
                CloseHandle(job);
                return null;
            }

            return new KillOnCloseJob(job);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
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
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
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

        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial IntPtr CreateJobObject(IntPtr attributes, string? name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);
    }
}
