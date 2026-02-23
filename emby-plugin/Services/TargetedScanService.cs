using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace EmbyTargetedScan.Services
{
    public class TargetedScanService
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _parentLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;

        public TargetedScanService(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            ILogger logger)
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
        private (List<string> MissingPaths, Folder KnownAncestor) WalkUpToAncestor(string path, Dictionary<string, BaseItem> cache = null)
        {
            var missingPaths = new List<string> { path };
            Folder knownAncestor = null;
            var current = Path.GetDirectoryName(path);

            while (!string.IsNullOrEmpty(current))
            {
                BaseItem found;
                if (cache != null && cache.TryGetValue(current, out var cached))
                {
                    found = cached;
                }
                else
                {
                    found = _libraryManager.FindByPath(current, null);
                    if (cache != null && found != null)
                    {
                        cache[current] = found;
                    }
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

        public ScanPathResult ScanPath(string path, Dictionary<string, BaseItem> cache = null)
        {
            _logger.Info("TargetedScan: scanning path {0}", path);

            // 1. Verify path exists on filesystem
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                // Check if there's a stale database entry to clean up (upgrade scenario:
                // Sonarr/Radarr deleted the old file and sent us the deleted path)
                var staleItem = _libraryManager.FindByPath(path, null);
                if (staleItem != null)
                {
                    _logger.Info(
                        "TargetedScan: removing stale item {0} ({1}) — file no longer exists: {2}",
                        staleItem.Name, staleItem.InternalId, path);
                    var parent = staleItem.GetParent();
                    _libraryManager.DeleteItem(staleItem, new DeleteOptions
                    {
                        DeleteFileLocation = false,
                        DeleteFromExternalProvider = false
                    }, parent, false);
                    if (cache != null)
                    {
                        cache.Remove(path);
                    }
                    return new ScanPathResult
                    {
                        Status = ScanStatus.Removed,
                        ItemId = staleItem.InternalId.ToString(),
                        ItemName = staleItem.Name
                    };
                }

                _logger.Warn("TargetedScan: path does not exist on filesystem: {0}", path);
                return new ScanPathResult { Status = ScanStatus.PathNotFound };
            }

            // 2. Check if item already exists in the database (use cache if available)
            BaseItem existing;
            if (cache != null && cache.TryGetValue(path, out var cachedItem))
            {
                existing = cachedItem;
            }
            else
            {
                existing = _libraryManager.FindByPath(path, null);
                if (cache != null && existing != null)
                {
                    cache[path] = existing;
                }
            }

            if (existing != null)
            {
                _logger.Info("TargetedScan: item already exists ({0}), queuing refresh", existing.InternalId);
                _providerManager.QueueRefresh(
                    existing.InternalId,
                    new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
                    {
                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                        ReplaceAllMetadata = true
                    },
                    RefreshPriority.High);

                return new ScanPathResult
                {
                    Status = ScanStatus.Refreshed,
                    ItemId = existing.InternalId.ToString(),
                    ItemName = existing.Name
                };
            }

            // 3. Walk up BEFORE acquiring lock (read-only, safe without lock)
            var (missingPaths, knownAncestor) = WalkUpToAncestor(path, cache);
            if (knownAncestor == null)
            {
                _logger.Error("TargetedScan: could not find any ancestor in database for path: {0}", path);
                return new ScanPathResult { Status = ScanStatus.ParentNotFound };
            }

            // 4. Acquire per-ancestor lock — different ancestors can proceed in parallel
            var lockKey = knownAncestor.Path;
            var parentLock = _parentLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
            _logger.Info("TargetedScan: acquiring lock for ancestor {0}", lockKey);
            parentLock.Wait();
            try
            {
                return CreateItems(path, missingPaths, knownAncestor, cache);
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
        public List<ScanPathResult> ScanPaths(IEnumerable<string> paths)
        {
            var unique = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
                .ToList();

            _logger.Info("TargetedScan: batch scanning {0} paths", unique.Count);

            var pathCache = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
            var results = new List<ScanPathResult>();

            foreach (var path in unique)
            {
                var result = ScanPath(path, pathCache);
                result.Path = path;
                results.Add(result);
            }

            return results;
        }

        private ScanPathResult CreateItems(string path, List<string> missingPaths, Folder knownAncestor, Dictionary<string, BaseItem> cache = null)
        {
            // Re-check after acquiring lock — another request may have created it
            var existing = _libraryManager.FindByPath(path, null);
            if (existing != null)
            {
                if (cache != null)
                {
                    cache[path] = existing;
                }

                _logger.Info("TargetedScan: item was created by concurrent request ({0}), queuing refresh", existing.InternalId);
                _providerManager.QueueRefresh(
                    existing.InternalId,
                    new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
                    {
                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                        ReplaceAllMetadata = true
                    },
                    RefreshPriority.High);

                return new ScanPathResult
                {
                    Status = ScanStatus.Refreshed,
                    ItemId = existing.InternalId.ToString(),
                    ItemName = existing.Name
                };
            }

            _logger.Info(
                "TargetedScan: found ancestor {0} ({1}), need to create {2} intermediate items",
                knownAncestor.Name, knownAncestor.InternalId, missingPaths.Count);

            // Create items from top down
            missingPaths.Reverse();

            var directoryService = new DirectoryService(_logger, _fileSystem);
            Folder currentParent = knownAncestor;
            BaseItem lastCreated = null;
            foreach (var missingPath in missingPaths)
            {
                // Re-check: a concurrent request (before we held the lock) may have created this
                var alreadyExists = _libraryManager.FindByPath(missingPath, null);
                if (alreadyExists != null)
                {
                    if (cache != null)
                    {
                        cache[missingPath] = alreadyExists;
                    }

                    _logger.Info("TargetedScan: {0} already exists (concurrent create), skipping", missingPath);
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
                var newItem = _libraryManager.ResolvePath(fileInfo, currentParent);

                if (newItem == null)
                {
                    _logger.Warn("TargetedScan: ResolvePath returned null for: {0}", missingPath);
                    return new ScanPathResult { Status = ScanStatus.Failed };
                }

                _logger.Info(
                    "TargetedScan: resolved {0} ({1}) under {2}",
                    newItem.Name, newItem.GetType().Name, currentParent.Name);

                _libraryManager.CreateItem(newItem, currentParent);
                _logger.Info("TargetedScan: created {0} ({1})", newItem.Name, newItem.InternalId);

                // Queue metadata refresh immediately so providers identify the item
                _providerManager.QueueRefresh(
                    newItem.InternalId,
                    new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
                    {
                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                        ReplaceAllMetadata = true
                    },
                    RefreshPriority.High);
                _logger.Info("TargetedScan: queued metadata refresh for {0}", newItem.Name);

                if (cache != null)
                {
                    cache[missingPath] = newItem;
                }

                lastCreated = newItem;

                if (newItem is Folder folder)
                {
                    currentParent = folder;
                }
                else
                {
                    _logger.Info("TargetedScan: item {0} is not a folder, stopping creation loop", newItem.Name);
                    break;
                }
            }

            if (lastCreated == null)
            {
                return new ScanPathResult { Status = ScanStatus.Failed };
            }

            // Queue metadata refresh for ancestors between knownAncestor and the library root.
            // When knownAncestor is a Season, this ensures the parent Series also gets
            // identified by providers — episode metadata depends on Series being identified first.
            // Skip library root folders (their parent is null or is a system root).
            var ancestorsToRefresh = new List<BaseItem>();
            var walkItem = knownAncestor as BaseItem;
            while (walkItem != null)
            {
                var parent = walkItem.GetParent();
                if (parent == null || parent is UserRootFolder || parent is AggregateFolder)
                    break;
                ancestorsToRefresh.Add(walkItem);
                walkItem = parent;
            }

            // Refresh top-down (Series before Season) so provider IDs cascade
            ancestorsToRefresh.Reverse();
            foreach (var ancestor in ancestorsToRefresh)
            {
                _providerManager.QueueRefresh(
                    ancestor.InternalId,
                    new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
                    {
                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                        ReplaceAllMetadata = false
                    },
                    RefreshPriority.High);
                _logger.Info("TargetedScan: queued ancestor refresh for {0} ({1})",
                    ancestor.Name, ancestor.GetType().Name);
            }

            return new ScanPathResult
            {
                Status = ScanStatus.Created,
                ItemId = lastCreated.InternalId.ToString(),
                ItemName = lastCreated.Name
            };
        }
    }

    public class ScanPathResult
    {
        public ScanStatus Status { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string Path { get; set; }
    }

    public enum ScanStatus
    {
        Created,
        Refreshed,
        PathNotFound,
        ParentNotFound,
        Failed,
        Discovered,
        Removed
    }
}
