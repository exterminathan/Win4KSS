using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Win4K.Core;

namespace Win4K.ScreenSaver;

/// <summary>
/// The resident "Start now" hotkey listener: <c>Win4KSS.exe /hotkey</c>.
///
/// Until v1.2.6 the hotkey was the Hotkey field of the Start Screen Saver .lnk files, which
/// Explorer only binds for shortcuts on the Desktop or in the Start Menu, so removing the
/// shortcuts removed the hotkey. It was also slow: Explorer launched a cold <c>/now</c> process,
/// which then asked Windows to start the saver (measured: the screen switched ~460-600 ms after
/// the key press, against ~90 ms from the browser's already-running process).
///
/// This process holds the hotkey with <see cref="RegisterHotKey"/> and calls
/// <see cref="SaverLauncher.StartNow"/> itself, already warm, so the screen saver starts as
/// fast as the browser's button and with exactly its semantics (Windows starts the configured
/// saver, which honours "On resume, display logon screen"). It has no window and no tray icon:
/// a thread message queue and two named events. It starts at sign-in from
/// HKCU\...\Run (no admin), registers that value itself, and removes it when the hotkey is
/// turned off. The browser saves a new hotkey and signals <see cref="ReloadEventName"/>; the
/// installer signals <see cref="StopEventName"/> before replacing files and starts it again after.
/// </summary>
internal static partial class HotkeyAgent
{
    public const string Argument = "/hotkey";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueBaseName = "Win4KSS";

    private const int HotkeyId = 0x5734;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    // ---- names --------------------------------------------------------------------------------

    /// <summary>The data root a normal run uses; any other root (WIN4K_HOME, a test) gets its own names.</summary>
    internal static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win4KScreensavers");

    /// <summary>"" for the normal data root, else "." plus a short hash of <paramref name="root"/>.</summary>
    internal static string Scope(string root, string defaultRoot)
    {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultRoot));
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return "";
        return "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(a.ToLowerInvariant())))[..8];
    }

    private static string CurrentScope => Scope(AppPaths.Root, DefaultRoot);

    /// <summary>A data root other than the normal one: WIN4K_HOME, as smoke runs and tests use.</summary>
    private static bool IsTestRoot => CurrentScope.Length > 0;

    /// <summary>Set to 1 to let a test data root write its own (separately named) Run value.</summary>
    public const string TestRunValueVariable = "WIN4K_HOTKEY_TEST_RUN_VALUE";

    /// <summary>Held for the agent's lifetime: one listener per session (and per data root).</summary>
    internal static string MutexNameFor(string scope) => @"Local\Win4KSS.Hotkey" + scope;
    internal static string MutexName => MutexNameFor(CurrentScope);
    internal static string ReloadEventName => MutexName + ".Reload";
    internal static string StopEventName => MutexName + ".Stop";

    /// <summary>The HKCU Run value: "Win4KSS" normally; a separate, clearly-named one for a test root.</summary>
    internal static string RunValueNameFor(string scope) =>
        scope.Length == 0 ? RunValueBaseName : RunValueBaseName + "-test-" + scope.TrimStart('.');
    internal static string RunValueName => RunValueNameFor(CurrentScope);

    internal static string RunCommand(string exe) => $"\"{exe}\" {Argument}";

    /// <summary>
    /// The exe the listener runs as: this build's own .exe, so a browser always starts a listener
    /// of its own version; the companion exe when this is the System32 .scr (never a .scr: the
    /// shell runs those with /S whatever the command line says).
    /// </summary>
    internal static string AgentExe(string? processPath, string companionExe) =>
        processPath is not null && processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processPath : companionExe;

    private static string CurrentAgentExe => AgentExe(Environment.ProcessPath, ShortcutManager.CompanionExePath);

    // ---- hotkey values ------------------------------------------------------------------------

    /// <summary>The hotkey in force: the default when never set, "" when turned off.</summary>
    public static string Effective(string? stored) =>
        stored is null ? ShortcutManager.DefaultHotkey : ShortcutManager.NormalizeHotkey(stored) ?? "";

    internal const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

    /// <summary>"CTRL+ALT+S" (or any form <see cref="ShortcutManager.NormalizeHotkey"/> accepts) as RegisterHotKey arguments.</summary>
    internal static bool TryParse(string? hotkey, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        var normalized = ShortcutManager.NormalizeHotkey(hotkey);
        if (string.IsNullOrEmpty(normalized))
            return false;

        foreach (var part in normalized.Split('+'))
        {
            switch (part)
            {
                case "CTRL": modifiers |= MOD_CONTROL; break;
                case "ALT": modifiers |= MOD_ALT; break;
                case "SHIFT": modifiers |= MOD_SHIFT; break;
                default:
                    if (!Enum.TryParse<Keys>(part, ignoreCase: true, out var key))
                        return false;
                    virtualKey = (uint)key; // Keys values are virtual-key codes
                    break;
            }
        }
        return modifiers != 0 && virtualKey != 0;
    }

    /// <summary>"CTRL+ALT+S" as people write it: "Ctrl+Alt+S".</summary>
    internal static string Display(string hotkey) => string.Join('+', hotkey.Split('+').Select(p => p switch
    {
        "CTRL" => "Ctrl",
        "ALT" => "Alt",
        "SHIFT" => "Shift",
        _ when p.Length == 2 && p[0] == 'D' && char.IsDigit(p[1]) => p[1..],
        _ when p.Length > 1 => p[0] + p[1..].ToLowerInvariant(),
        _ => p,
    }));

    // ---- migration from the .lnk Hotkey field ---------------------------------------------------

    /// <summary>
    /// Moves the hotkey off the Start Screen Saver shortcuts. While a shortcut carries it,
    /// Explorer holds the combination (RegisterHotKey fails with 1409), and both would fire.
    /// Adopts the shortcuts' hotkey when the setting was never set, then clears the field on
    /// every shortcut that has one; Explorer lets the combination go at once (measured).
    /// </summary>
    /// <param name="settingsFileExisted">
    /// False on a fresh install. Shortcuts with no hotkey on an existing install mean the user
    /// had turned it off; on a fresh one they mean nothing.
    /// </param>
    /// <returns>True if <paramref name="settings"/> changed and needs saving.</returns>
    internal static bool MigrateShortcutHotkeys(
        AppSettings settings,
        bool settingsFileExisted,
        IReadOnlyList<string> shortcuts,
        Func<string, bool> exists,
        Func<string, string?> readHotkey,
        Action<string> clearHotkey)
    {
        var present = shortcuts.Where(exists).ToList();
        var carrying = present.Select(p => (Path: p, Hotkey: readHotkey(p))).Where(s => s.Hotkey is not null).ToList();

        var changed = false;
        if (settings.StartNowHotkey is null)
        {
            var adopted = carrying.Select(s => ShortcutManager.NormalizeHotkey(s.Hotkey)).FirstOrDefault(h => !string.IsNullOrEmpty(h));
            settings.StartNowHotkey = adopted
                ?? (carrying.Count == 0 && present.Count > 0 && settingsFileExisted ? "" : ShortcutManager.DefaultHotkey);
            changed = true;
        }

        foreach (var (path, _) in carrying)
            clearHotkey(path);

        return changed;
    }

    private static void MigrateAndSave()
    {
        try
        {
            var existed = File.Exists(AppPaths.SettingsFile);
            var settings = AppSettings.Load();

            // The shortcuts are the machine's real ones whatever the data root, so a test or
            // smoke run on another root (WIN4K_HOME) reads them but never rewrites them.
            Action<string> clear = IsTestRoot
                ? path => Log.Info($"Hotkey: test data root; leaving the hotkey field on {path}")
                : ShortcutManager.ClearHotkey;

            if (MigrateShortcutHotkeys(settings, existed, [ShortcutManager.DesktopPath, ShortcutManager.StartMenuPath],
                                       File.Exists, ShortcutManager.ReadHotkey, clear))
            {
                settings.Save();
                Log.Info($"Hotkey: moved off the shortcuts into settings ({Describe(settings.StartNowHotkey)})");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Hotkey: could not migrate the shortcut hotkey", ex);
        }
    }

    private static string Describe(string? hotkey) => string.IsNullOrEmpty(hotkey) ? "none" : Display(hotkey);

    // ---- control from other processes (the browser, the settings CLI) --------------------------

    public static bool IsRunning
    {
        get
        {
            if (!Mutex.TryOpenExisting(MutexName, out var mutex))
                return false;
            mutex.Dispose();
            return true;
        }
    }

    /// <summary>
    /// Puts a hotkey just saved to settings.json into effect, without signing out: the running
    /// listener re-reads it, or one is started; "" stops it and removes its Run value.
    /// </summary>
    /// <param name="previous">The hotkey in force before, which the listener itself still holds.</param>
    /// <returns>A problem worth telling the user, or null.</returns>
    public static string? Apply(string hotkey, string? previous)
    {
        try
        {
            string? problem = null;
            if (hotkey.Length > 0 && TryParse(hotkey, out var mods, out var vk) &&
                !string.Equals(hotkey, previous, StringComparison.OrdinalIgnoreCase) && !IsFree(mods, vk))
            {
                problem = $"{Display(hotkey)} is already used by another app, so it may not start the screen saver. Pick another combination.";
                Log.Warn($"Hotkey: {Display(hotkey)} is held by another app");
            }

            if (IsRunning)
            {
                Signal(ReloadEventName);
                Log.Info($"Hotkey: asked the listener to switch to {Describe(hotkey)}");
            }
            else if (hotkey.Length > 0)
            {
                Start();
            }
            else
            {
                SetRunValue(enabled: false);
            }
            return problem;
        }
        catch (Exception ex)
        {
            Log.Error("Hotkey: could not apply the new hotkey", ex);
            return "The hotkey could not be updated; see win4k.log.";
        }
    }

    /// <summary>Starts the listener (it exits at once if the hotkey is off or one is already running).</summary>
    public static bool Start()
    {
        var exe = CurrentAgentExe;
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            start.ArgumentList.Add(Argument);
            using var process = Process.Start(start);
            Log.Info($"Hotkey: started the listener ({exe} {Argument}, pid {process?.Id})");
            return process is not null;
        }
        catch (Exception ex)
        {
            Log.Error($"Hotkey: could not start {exe} {Argument}", ex);
            return false;
        }
    }

    /// <summary>Whether nobody (Explorer, another app) holds this combination right now.</summary>
    private static bool IsFree(uint modifiers, uint virtualKey)
    {
        const int probeId = 0x5735;
        if (!RegisterHotKey(IntPtr.Zero, probeId, modifiers | MOD_NOREPEAT, virtualKey))
            return false;
        UnregisterHotKey(IntPtr.Zero, probeId);
        return true;
    }

    private static void Signal(string eventName)
    {
        if (EventWaitHandle.TryOpenExisting(eventName, out var handle))
        {
            using (handle)
                handle.Set();
        }
    }

    /// <summary>Writes (or removes) this session's HKCU Run value for the listener.</summary>
    internal static void SetRunValue(bool enabled)
    {
        if (IsTestRoot && Environment.GetEnvironmentVariable(TestRunValueVariable) != "1")
        {
            Log.Info($"Hotkey: test data root; not touching HKCU Run (set {TestRunValueVariable}=1 to use '{RunValueName}')");
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            var name = RunValueName;
            if (enabled)
            {
                var command = RunCommand(CurrentAgentExe);
                if (!string.Equals(key.GetValue(name) as string, command, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(name, command, RegistryValueKind.String);
                    Log.Info($"Hotkey: starts at sign-in (HKCU Run '{name}' = {command})");
                }
            }
            else if (key.GetValue(name) is not null)
            {
                key.DeleteValue(name, throwOnMissingValue: false);
                Log.Info($"Hotkey: no longer starts at sign-in (removed HKCU Run '{name}')");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Hotkey: could not update the HKCU Run value", ex);
        }
    }

    // ---- the listener -------------------------------------------------------------------------

    private static int _busy;

    /// <summary>The /hotkey verb. Returns when stopped, or at once if the hotkey is off.</summary>
    public static int Run()
    {
        var started = Stopwatch.StartNew();
        MigrateAndSave();

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            Signal(ReloadEventName);
            Log.Info("Hotkey: a listener is already running; asked it to re-read the hotkey");
            return 0;
        }

        using var reload = new EventWaitHandle(false, EventResetMode.AutoReset, ReloadEventName);
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);

        string? held = null;
        var registered = false;
        var warned = false;

        // Returns false when the hotkey is off and the listener should end.
        bool Load()
        {
            var hotkey = Effective(AppSettings.Load().StartNowHotkey);
            if (registered && string.Equals(hotkey, held, StringComparison.Ordinal))
                return true;

            if (registered)
            {
                UnregisterHotKey(IntPtr.Zero, HotkeyId);
                registered = false;
            }
            held = hotkey;
            warned = false;

            if (hotkey.Length == 0 || !TryParse(hotkey, out _, out _))
            {
                SetRunValue(enabled: false);
                Log.Info($"Hotkey: {(hotkey.Length == 0 ? "turned off" : $"'{hotkey}' is not usable")}; the listener is exiting");
                return false;
            }

            SetRunValue(enabled: true);
            TryRegister();
            return true;
        }

        void TryRegister()
        {
            if (registered || held is null || !TryParse(held, out var mods, out var vk))
                return;
            if (RegisterHotKey(IntPtr.Zero, HotkeyId, mods | MOD_NOREPEAT, vk))
            {
                registered = true;
                Log.Info($"Hotkey: listening for {Display(held)} (ready {started.ElapsedMilliseconds} ms after start)");
                Trim();
            }
            else if (!warned)
            {
                warned = true;
                Log.Warn($"Hotkey: {Display(held)} is held by another app (error {Marshal.GetLastPInvokeError()}); retrying every {RetryInterval.TotalSeconds:0} s");
            }
        }

        if (!Load())
            return 0;

        var handles = new[] { stop.SafeWaitHandle.DangerousGetHandle(), reload.SafeWaitHandle.DangerousGetHandle() };
        while (true)
        {
            var timeout = registered ? INFINITE : (uint)RetryInterval.TotalMilliseconds;
            var result = MsgWaitForMultipleObjectsEx((uint)handles.Length, handles, timeout, QS_ALLINPUT, MWMO_INPUTAVAILABLE);

            if (result == WAIT_OBJECT_0)
            {
                Log.Info("Hotkey: stop requested; the listener is exiting");
                break;
            }

            if (result == WAIT_OBJECT_0 + 1)
            {
                if (!Load())
                    break;
                continue;
            }

            if (result == WAIT_TIMEOUT)
            {
                TryRegister();
                continue;
            }

            if (result == WAIT_FAILED)
            {
                Log.Error($"Hotkey: wait failed (error {Marshal.GetLastPInvokeError()}); the listener is exiting");
                break;
            }

            var quit = false;
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT)
                {
                    quit = true;
                }
                else if (msg.message == WM_HOTKEY && (int)msg.wParam == HotkeyId)
                {
                    OnHotkey();
                }
                else
                {
                    // Anything else belongs to a window on this thread (COM's, in this STA).
                    TranslateMessage(in msg);
                    DispatchMessage(in msg);
                }
            }
            if (quit)
                break;
        }

        if (registered)
            UnregisterHotKey(IntPtr.Zero, HotkeyId);
        GC.KeepAlive(mutex);
        return 0;
    }

    private static void OnHotkey()
    {
        // A held key does not repeat (MOD_NOREPEAT), and a press while one start is still
        // being waited on is the same request.
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;

        // This process just received the input, so it may hand the foreground on: a saver it
        // has to run directly then gets the keyboard as well as the screen.
        NativeMethods.AllowAnyForeground();

        var pressed = Stopwatch.StartNew();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Log.Info("Hotkey: pressed; starting the screen saver");
                var ok = SaverLauncher.StartNow();
                Log.Info($"Hotkey: start {(ok ? "done" : "FAILED")} {pressed.ElapsedMilliseconds} ms after the press");
            }
            catch (Exception ex)
            {
                Log.Error("Hotkey: starting the screen saver failed", ex);
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
                Trim();
            }
        });
    }

    /// <summary>Gives back the working set start-up used; it idles for hours, so pages come back only on a press.</summary>
    private static void Trim()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        }
        catch (Exception)
        {
            // Purely cosmetic.
        }
    }

    // ---- native -------------------------------------------------------------------------------

    private const uint WM_QUIT = 0x0012;
    private const uint WM_HOTKEY = 0x0312;
    private const uint PM_REMOVE = 0x0001;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x0004;
    private const uint WAIT_OBJECT_0 = 0;
    private const uint WAIT_TIMEOUT = 0x102;
    private const uint WAIT_FAILED = 0xFFFFFFFF;
    private const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in MSG msg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial IntPtr DispatchMessage(in MSG msg);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
