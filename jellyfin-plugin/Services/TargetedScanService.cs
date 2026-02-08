using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace JellyfinTargetedScan.Services;

/// <summary>
/// Core logic for targeted library scanning.
/// Uses ResolvePath + CreateItem for instant item creation without ValidateChildren.
/// Walks up the directory tree to find the nearest known ancestor, then creates
/// all intermediate items (show, season, episode) from the top down.
/// </summary>
public class TargetedScanService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _parentLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _validateLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<TargetedScanService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TargetedScanService"/> class.
    /// </summary>
    public TargetedScanService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<TargetedScanService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Walk up the directory tree to find the nearest known ancestor.
    /// This is a read-only operation safe to call without holding any lock.
    /// </summary>
    private (List<string> MissingPaths, Folder? KnownAncestor) WalkUpToAncestor(string path, Dictionary<string, BaseItem?>? cache = null)
    {
        var missingPaths = new List<string> { path };
        Folder? knownAncestor = null;
        var current = Path.GetDirectoryName(path);

        while (!string.IsNullOrEmpty(current))
        {
            BaseItem? found;
            if (cache != null && cache.TryGetValue(current, out var cached))
            {
                found = cached;
            }
            else
            {
                found = _libraryManager.FindByPath(current, null);
                cache?.TryAdd(current, found);
            }

            if (found is Folder folder)
            {
                knownAncestor = folder;
                break;
            }

            missingPaths.Add(current);
            current = Path.GetDirectoryName(current);
        }

        return (missingPaths, knownAncestor);
    }

    /// <summary>
    /// Scan a specific path, creating the item if new, or refreshing if existing.
    /// </summary>
    /// <param name="path">Filesystem path to scan.</param>
    /// <param name="cache">Optional FindByPath cache shared across a batch.</param>
    /// <returns>The scanned or created item.</returns>
    public async Task<ScanPathResult> ScanPathAsync(string path, Dictionary<string, BaseItem?>? cache = null)
    {
        _logger.LogInformation("TargetedScan: scanning path {Path}", path);

        // 1. Verify path exists on filesystem
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            _logger.LogWarning("TargetedScan: path does not exist on filesystem: {Path}", path);
            return new ScanPathResult { Status = ScanStatus.PathNotFound };
        }

        // 2. Check if item already exists in the database (use cache if available)
        BaseItem? existing;
        if (cache != null && cache.TryGetValue(path, out var cachedItem))
        {
            existing = cachedItem;
        }
        else
        {
            existing = _libraryManager.FindByPath(path, null);
            cache?.TryAdd(path, existing);
        }

        if (existing != null)
        {
            _logger.LogInformation("TargetedScan: item already exists ({Id}), queuing refresh", existing.Id);
            _providerManager.QueueRefresh(
                existing.Id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllMetadata = false
                },
                RefreshPriority.High);

            return new ScanPathResult
            {
                Status = ScanStatus.Refreshed,
                ItemId = existing.Id.ToString("N"),
                ItemName = existing.Name
            };
        }

        // 3. Walk up BEFORE acquiring lock (read-only, safe without lock)
        var (missingPaths, knownAncestor) = WalkUpToAncestor(path, cache);
        if (knownAncestor == null)
        {
            _logger.LogError("TargetedScan: could not find any ancestor in database for path: {Path}", path);
            return new ScanPathResult { Status = ScanStatus.ParentNotFound };
        }

        // 4. Acquire per-ancestor lock — different ancestors can proceed in parallel
        var lockKey = knownAncestor.Path;
        var parentLock = _parentLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        _logger.LogInformation("TargetedScan: acquiring lock for ancestor {LockKey}", lockKey);
        await parentLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await CreateItemsAsync(path, missingPaths, knownAncestor, cache).ConfigureAwait(false);
        }
        finally
        {
            parentLock.Release();
            if (parentLock.CurrentCount == 1 && _parentLocks.Count > 50)
            {
                _parentLocks.TryRemove(lockKey, out _);
            }
        }
    }

    /// <summary>
    /// Scan multiple paths in a single batch. Deduplicates by parent directory —
    /// only one representative per parent is scanned, since the parent's metadata
    /// refresh will auto-discover all sibling items in the same folder.
    /// </summary>
    public async Task<List<ScanPathResult>> ScanPathsAsync(IEnumerable<string> paths)
    {
        var unique = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Group by parent directory — only scan one representative per parent
        var groups = unique
            .GroupBy(p => Path.GetDirectoryName(p) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var representatives = groups
            .Select(g => g.First())
            .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
            .ToList();

        var siblingPaths = new HashSet<string>(
            unique.Except(representatives, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "TargetedScan: batch scanning {Total} paths ({Reps} representatives, {Siblings} siblings auto-discovered)",
            unique.Count, representatives.Count, siblingPaths.Count);

        var pathCache = new Dictionary<string, BaseItem?>(StringComparer.OrdinalIgnoreCase);
        var results = new List<ScanPathResult>();

        // Scan representatives
        foreach (var path in representatives)
        {
            var result = await ScanPathAsync(path, pathCache).ConfigureAwait(false);
            result.Path = path;
            results.Add(result);
        }

        // Mark siblings as Discovered — parent metadata refresh will find them
        foreach (var path in siblingPaths)
        {
            results.Add(new ScanPathResult
            {
                Status = ScanStatus.Discovered,
                Path = path,
                ItemName = Path.GetFileNameWithoutExtension(path)
            });
        }

        return results;
    }

    private Task<ScanPathResult> CreateItemsAsync(string path, List<string> missingPaths, Folder knownAncestor, Dictionary<string, BaseItem?>? cache = null)
    {
        // Re-check after acquiring lock — another request may have created it
        var existing = _libraryManager.FindByPath(path, null);
        if (existing != null)
        {
            cache?.Remove(path);
            cache?.TryAdd(path, existing);

            _logger.LogInformation("TargetedScan: item was created by concurrent request ({Id}), scheduling ValidateChildren in background", existing.Id);
            var parentFolder = existing.GetParent() as Folder ?? knownAncestor;
            _ = Task.Run(async () =>
            {
                var vLock = _validateLocks.GetOrAdd(parentFolder.Path, _ => new SemaphoreSlim(1, 1));
                await vLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await parentFolder.ValidateChildren(
                        new Progress<double>(),
                        new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                        {
                            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                            ReplaceAllMetadata = false
                        },
                        recursive: false,
                        allowRemoveRoot: false,
                        CancellationToken.None).ConfigureAwait(false);
                    _logger.LogInformation("TargetedScan: background ValidateChildren completed on {ParentName}", parentFolder.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "TargetedScan: background ValidateChildren failed on {ParentName}", parentFolder.Name);
                }
                finally
                {
                    vLock.Release();
                    if (vLock.CurrentCount == 1 && _validateLocks.Count > 50)
                    {
                        _validateLocks.TryRemove(parentFolder.Path, out _);
                    }
                }
            });

            return Task.FromResult(new ScanPathResult
            {
                Status = ScanStatus.Refreshed,
                ItemId = existing.Id.ToString("N"),
                ItemName = existing.Name
            });
        }

        _logger.LogInformation(
            "TargetedScan: found ancestor {AncestorName} ({AncestorId}), need to create {Count} intermediate items",
            knownAncestor.Name, knownAncestor.Id, missingPaths.Count);

        // Create items from top down (reverse the list so we go ancestor → target)
        missingPaths.Reverse();

        var directoryService = new DirectoryService(_fileSystem);
        Folder currentParent = knownAncestor;
        BaseItem? lastCreated = null;

        foreach (var missingPath in missingPaths)
        {
            // Re-check: a concurrent request (before we held the lock) may have created this
            var alreadyExists = _libraryManager.FindByPath(missingPath, null);
            if (alreadyExists != null)
            {
                cache?.Remove(missingPath);
                cache?.TryAdd(missingPath, alreadyExists);

                _logger.LogInformation("TargetedScan: {Path} already exists (concurrent create), skipping", missingPath);
                if (alreadyExists is Folder existingFolder)
                {
                    currentParent = existingFolder;
                    lastCreated = alreadyExists;
                    continue;
                }
                else
                {
                    lastCreated = alreadyExists;
                    break;
                }
            }

            var fileInfo = _fileSystem.GetFileSystemInfo(missingPath);
            var newItem = _libraryManager.ResolvePath(fileInfo, currentParent, directoryService);

            if (newItem == null)
            {
                _logger.LogWarning("TargetedScan: ResolvePath returned null for: {Path}", missingPath);
                return Task.FromResult(new ScanPathResult { Status = ScanStatus.Failed });
            }

            _logger.LogInformation(
                "TargetedScan: resolved {ItemName} ({ItemType}) under {ParentName}",
                newItem.Name, newItem.GetType().Name, currentParent.Name);

            _libraryManager.CreateItem(newItem, currentParent);
            _logger.LogInformation("TargetedScan: created {ItemName} ({ItemId})", newItem.Name, newItem.Id);

            // Queue metadata refresh immediately so providers identify the item
            _providerManager.QueueRefresh(
                newItem.Id,
                new MetadataRefreshOptions(directoryService)
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllMetadata = true
                },
                RefreshPriority.High);
            _logger.LogInformation("TargetedScan: queued metadata refresh for {ItemName}", newItem.Name);

            cache?.TryAdd(missingPath, newItem);
            lastCreated = newItem;

            if (newItem is Folder folder)
            {
                currentParent = folder;
            }
            else
            {
                _logger.LogInformation("TargetedScan: item {ItemName} is not a folder, stopping creation loop", newItem.Name);
                break;
            }
        }

        if (lastCreated == null)
        {
            return Task.FromResult(new ScanPathResult { Status = ScanStatus.Failed });
        }

        // Queue metadata refresh for the known ancestor and all parents up the chain.
        // When knownAncestor is a Season, this ensures the parent Series also gets
        // identified by providers — episode metadata depends on Series being identified first.
        var ancestorsToRefresh = new List<BaseItem>();
        var walkItem = knownAncestor as BaseItem;
        while (walkItem != null)
        {
            if (walkItem is CollectionFolder || walkItem is UserRootFolder || walkItem is AggregateFolder)
                break;
            ancestorsToRefresh.Add(walkItem);
            walkItem = walkItem.GetParent();
        }

        // Refresh top-down (Series before Season) so provider IDs cascade
        ancestorsToRefresh.Reverse();
        foreach (var ancestor in ancestorsToRefresh)
        {
            _providerManager.QueueRefresh(
                ancestor.Id,
                new MetadataRefreshOptions(directoryService)
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllMetadata = false
                },
                RefreshPriority.High);
            _logger.LogInformation("TargetedScan: queued ancestor refresh for {Name} ({Type})",
                ancestor.Name, ancestor.GetType().Name);
        }

        // Fire-and-forget ValidateChildren on the ancestor to properly register items
        // in parent cache and trigger metadata refresh. Runs in background so the API
        // response returns immediately (ValidateChildren can take 30+ seconds).
        _logger.LogInformation("TargetedScan: scheduling ValidateChildren on {AncestorName} in background", knownAncestor.Name);
        _ = Task.Run(async () =>
        {
            var vLock = _validateLocks.GetOrAdd(knownAncestor.Path, _ => new SemaphoreSlim(1, 1));
            await vLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await knownAncestor.ValidateChildren(
                    new Progress<double>(),
                    new MetadataRefreshOptions(directoryService)
                    {
                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                        ReplaceAllMetadata = true
                    },
                    recursive: true,
                    allowRemoveRoot: false,
                    CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("TargetedScan: background ValidateChildren completed on {AncestorName}", knownAncestor.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TargetedScan: background ValidateChildren failed on {AncestorName}", knownAncestor.Name);
            }
            finally
            {
                vLock.Release();
                if (vLock.CurrentCount == 1 && _validateLocks.Count > 50)
                {
                    _validateLocks.TryRemove(knownAncestor.Path, out _);
                }
            }
        });

        return Task.FromResult(new ScanPathResult
        {
            Status = ScanStatus.Created,
            ItemId = lastCreated.Id.ToString("N"),
            ItemName = lastCreated.Name
        });
    }
}

/// <summary>
/// Result of a targeted scan operation.
/// </summary>
public class ScanPathResult
{
    /// <summary>
    /// Gets or sets the status.
    /// </summary>
    public ScanStatus Status { get; set; }

    /// <summary>
    /// Gets or sets the item ID.
    /// </summary>
    public string? ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item name.
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// Gets or sets the path (used in batch results).
    /// </summary>
    public string? Path { get; set; }
}

/// <summary>
/// Status of the scan operation.
/// </summary>
public enum ScanStatus
{
    /// <summary>Created a new item.</summary>
    Created,

    /// <summary>Refreshed an existing item.</summary>
    Refreshed,

    /// <summary>Path not found on filesystem.</summary>
    PathNotFound,

    /// <summary>Parent folder not found in database.</summary>
    ParentNotFound,

    /// <summary>Scan failed.</summary>
    Failed,

    /// <summary>Sibling will be auto-discovered by parent metadata refresh.</summary>
    Discovered
}
