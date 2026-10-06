using System.Collections.Concurrent;
using DataBridge.Core.IO;

namespace DataBridge.Core.Scanning;

/// <summary>Recursive totals of a directory. Cloud-only files are counted separately (their bytes are not on disk).</summary>
public sealed record DirStats(long Bytes, long Files, long Directories, long CloudBytes, long CloudFiles, int Errors)
{
    public static readonly DirStats Empty = new(0, 0, 0, 0, 0, 0);
    public long LocalBytes => Bytes - CloudBytes;
    public long LocalFiles => Files - CloudFiles;
}

/// <summary>
/// Computes directory sizes in the background. Each directory is computed once and cached
/// (concurrent requests for the same folder wait for the same computation). Links/junctions are not followed.
/// Only per-directory totals are kept in memory, never the file lists.
/// </summary>
public sealed class SizeScanner : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<DirStats>> _cache = new(PathUtil.Comparer);
    private readonly BlockingCollection<string> _priority = new(new ConcurrentQueue<string>());
    private readonly BlockingCollection<string> _background = new(new ConcurrentQueue<string>());
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Thread> _workers = [];
    private long _filesSeen;
    private int _pending;

    public SizeScanner(int workers = 2)
    {
        for (var i = 0; i < workers; i++)
        {
            var t = new Thread(WorkerLoop) { IsBackground = true, Name = $"SizeScanner-{i}", Priority = ThreadPriority.BelowNormal };
            _workers.Add(t);
            t.Start();
        }
    }

    /// <summary>Files counted so far (for an "analyse en cours" indicator).</summary>
    public long FilesSeen => Interlocked.Read(ref _filesSeen);

    /// <summary>True while queued or running scans remain.</summary>
    public bool IsBusy => Volatile.Read(ref _pending) > 0;

    /// <summary>Queues a background computation. Priority requests (folders the technician is looking at) go first.</summary>
    public void Enqueue(string directory, bool priority = false)
    {
        if (TryGet(directory, out _)) return;
        Interlocked.Increment(ref _pending);
        (priority ? _priority : _background).Add(directory);
    }

    /// <summary>Non-blocking: stats if already computed.</summary>
    public bool TryGet(string directory, out DirStats stats)
    {
        if (_cache.TryGetValue(directory, out var lazy) && lazy.IsValueCreated)
        {
            stats = lazy.Value;
            return true;
        }
        stats = DirStats.Empty;
        return false;
    }

    /// <summary>Blocking: computes (or waits for) the stats of a directory.</summary>
    public DirStats GetOrCompute(string directory) =>
        _cache.GetOrAdd(directory, d => new Lazy<DirStats>(() => Compute(d), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Forgets everything (used by "Actualiser").</summary>
    public void Invalidate() => _cache.Clear();

    private DirStats Compute(string directory)
    {
        long bytes = 0, files = 0, dirs = 0, cloudBytes = 0, cloudFiles = 0;
        var errors = 0;
        try
        {
            foreach (var e in FastDirectory.Enumerate(directory))
            {
                if (_stop.IsCancellationRequested) break;
                if (e.IsLink) continue;
                if (e.IsDirectory)
                {
                    var s = GetOrCompute(e.FullPath);
                    bytes += s.Bytes; files += s.Files; dirs += s.Directories + 1;
                    cloudBytes += s.CloudBytes; cloudFiles += s.CloudFiles; errors += s.Errors;
                }
                else
                {
                    bytes += e.Size;
                    files++;
                    if (e.IsCloudOnly) { cloudBytes += e.Size; cloudFiles++; }
                    Interlocked.Increment(ref _filesSeen);
                }
            }
        }
        catch (FsException)
        {
            errors++;
        }
        catch (IOException)
        {
            errors++;
        }
        catch (UnauthorizedAccessException)
        {
            errors++;
        }
        return new DirStats(bytes, files, dirs, cloudBytes, cloudFiles, errors);
    }

    private void WorkerLoop()
    {
        var queues = new[] { _priority, _background };
        while (!_stop.IsCancellationRequested)
        {
            string directory;
            try
            {
                BlockingCollection<string>.TakeFromAny(queues, out directory!, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                GetOrCompute(directory);
            }
            catch
            {
                // Compute() already absorbs IO errors; never let a worker die.
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
    }
}
