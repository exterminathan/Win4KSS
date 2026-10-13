using System.Security.Cryptography;
using System.Text;

namespace Win4K.Core.Caching;

/// <summary>
/// Names of the named kernel objects the cache shares between processes. The clip browser
/// and the saver can run at the same time (the idle timer fires while the browser is open),
/// and both read and write the same cache directory.
///
/// Every name carries a short hash of the cache root, so two caches in different folders
/// (and the test suite's per-test temp roots) never contend with each other. For the default
/// cache the names look like <c>Local\Win4KSS.CacheIndex.1a2b3c4d</c>.
/// </summary>
public static class CacheMutexNames
{
    public static string CacheIndex(string cacheRoot) => @"Local\Win4KSS.CacheIndex." + RootHash(cacheRoot);

    public static string DownloadQueueOwner(string cacheRoot) => @"Local\Win4KSS.DownloadQueueOwner." + RootHash(cacheRoot);

    internal static string RootHash(string cacheRoot)
    {
        var normal = Path.GetFullPath(cacheRoot).TrimEnd('\\', '/').ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normal)))[..8].ToLowerInvariant();
    }
}

/// <summary>
/// A named mutex used as a short, synchronous critical section across processes (and across
/// threads in this one — a Mutex is owned per thread). Every read-modify-write of
/// cache-index.json and queue.json happens inside one.
///
/// Never hold it across an await: a Mutex must be released by the thread that took it.
/// </summary>
internal sealed class CrossProcessLock(string name) : IDisposable
{
    private readonly Mutex _mutex = new(initiallyOwned: false, name);

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public string Name { get; } = name;

    public Releaser Acquire() => Acquire(DefaultTimeout);

    public Releaser Acquire(TimeSpan timeout)
    {
        try
        {
            if (!_mutex.WaitOne(timeout))
                throw new TimeoutException($"Timed out waiting for {Name}");
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died mid-write. We own it now; the files it guards are
            // written temp-then-move, so the worst it left behind is a stray .tmp.
            Log.Warn($"Cache: {Name} was abandoned by another process; continuing");
        }
        return new Releaser(_mutex);
    }

    public void Dispose() => _mutex.Dispose();

    public readonly struct Releaser(Mutex mutex) : IDisposable
    {
        public void Dispose() => mutex.ReleaseMutex();
    }
}

/// <summary>
/// Holds a named mutex for as long as it lives, on a dedicated thread — the owner of the
/// download queue is an async object whose continuations hop threads, and a Mutex can only
/// be released by the thread that acquired it.
/// </summary>
internal sealed class MutexLease : IDisposable
{
    private readonly ManualResetEventSlim _release;
    private readonly Thread _thread;

    private MutexLease(ManualResetEventSlim release, Thread thread)
    {
        _release = release;
        _thread = thread;
    }

    /// <summary>Takes the mutex if nobody holds it; null (immediately) if somebody does.</summary>
    public static MutexLease? TryAcquire(string name)
    {
        var decided = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var acquired = false;

        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, name);
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // A previous owner was killed; the lease is ours.
                acquired = true;
            }

            decided.Set();
            if (!acquired)
                return;

            release.Wait();
            mutex.ReleaseMutex();
        })
        {
            IsBackground = true,
            Name = "Win4K mutex lease",
        };

        thread.Start();
        decided.Wait();
        decided.Dispose();

        if (acquired)
            return new MutexLease(release, thread);

        thread.Join();
        release.Dispose();
        return null;
    }

    public void Dispose()
    {
        _release.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
        _release.Dispose();
    }
}

/// <summary>
/// Watches one JSON file for writes from another process and calls back once the writes
/// settle. Debounced, because a temp-then-move save raises several notifications.
/// The callback runs on a thread-pool thread.
/// </summary>
internal sealed class DebouncedFileWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _timer;
    private readonly string _fileName;
    private readonly TimeSpan _debounce;
    private volatile bool _disposed;

    public DebouncedFileWatcher(string directory, string fileName, TimeSpan debounce, Action onChanged)
    {
        _fileName = fileName;
        _debounce = debounce;
        _timer = new Timer(_ =>
        {
            if (_disposed)
                return;
            try
            {
                onChanged();
            }
            catch (Exception ex)
            {
                Log.Error($"Cache: handling a change to {fileName} failed", ex);
            }
        });

        try
        {
            Directory.CreateDirectory(directory);

            // No Filter: a temp-then-move save is a rename *to* the watched name, and matching
            // by hand on both names is more dependable than trusting the pattern to.
            _watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += OnEvent;
            _watcher.Created += OnEvent;
            _watcher.Renamed += OnEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // Without a watcher we only miss live updates from the other process; each
            // read-modify-write still reloads from disk, so nothing is lost.
            Log.Warn($"Cache: cannot watch {Path.Combine(directory, fileName)}: {ex.Message}");
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        var matches = string.Equals(e.Name, _fileName, StringComparison.OrdinalIgnoreCase) ||
                      (e is RenamedEventArgs r && string.Equals(r.OldName, _fileName, StringComparison.OrdinalIgnoreCase));
        if (!matches || _disposed)
            return;

        try
        {
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Raced with Dispose.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _timer.Dispose();
    }
}

/// <summary>
/// Temp-then-move writes and reads of the shared JSON files, retried briefly on sharing
/// violations. Anything may hold one of them open for a moment — the other process reading
/// it, antivirus, the search indexer — and a replace that fails then must not silently drop
/// the update (a lost queue.json write left a finished pin listed forever).
/// </summary>
internal static class SharedFile
{
    private const int Attempts = 40;
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(25);

    public static void Write(string path, string text)
    {
        var temp = path + ".tmp";
        Retry(() => File.WriteAllText(temp, text));
        Retry(() => File.Move(temp, path, overwrite: true));
    }

    /// <summary>The file's text, or null if it does not exist.</summary>
    public static string? Read(string path)
    {
        string? text = null;
        Retry(() => text = File.Exists(path) ? File.ReadAllText(path) : null);
        return text;
    }

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < Attempts)
            {
                Thread.Sleep(Pause);
            }
        }
    }
}

internal static class FileErrors
{
    /// <summary>ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION: another handle has the file.</summary>
    public static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    public static string Fingerprint(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
