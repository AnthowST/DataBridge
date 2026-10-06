using System.Collections.Concurrent;
using DataBridge.Core.IO;

namespace DataBridge.Core.Scanning;

/// <summary>Result of listing one directory (immediate children only).</summary>
public sealed record DirectoryListing(string Path, IReadOnlyList<FsEntry> Entries, int Error)
{
    public bool Failed => Error != 0;
}

/// <summary>
/// Caches the immediate children of directories that were actually opened (explorer, selection rules).
/// Nothing is listed recursively up front: this is the lazy-loading backbone of the explorer.
/// </summary>
public sealed class DirectoryListingCache
{
    private readonly ConcurrentDictionary<string, DirectoryListing> _cache = new(PathUtil.Comparer);

    public DirectoryListing Get(string directory)
    {
        if (_cache.TryGetValue(directory, out var cached)) return cached;
        var entries = FastDirectory.TryList(directory, out var error);
        var listing = new DirectoryListing(directory, entries ?? [], entries is null ? (error == 0 ? -1 : error) : 0);
        _cache[directory] = listing;
        return listing;
    }

    public bool TryGetCached(string directory, out DirectoryListing listing) => _cache.TryGetValue(directory, out listing!);

    public void Invalidate() => _cache.Clear();
}
