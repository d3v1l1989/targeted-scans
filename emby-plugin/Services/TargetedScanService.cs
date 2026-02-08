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
using MediaBrowser.Model.Logging;

namespace EmbyTargetedScan.Services
{
    public class TargetedScanService
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _parentLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _validateLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

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
                        ReplaceAllMetadata = false
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

            _logger.Info(
                "TargetedScan: batch scanning {0} paths ({1} representatives, {2} siblings auto-discovered)",
                unique.Count, representatives.Count, siblingPaths.Count);

            var pathCache = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
            var results = new List<ScanPathResult>();

            // Scan representatives
            foreach (var path in representatives)
            {
                var result = ScanPath(path, pathCache);
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

                _logger.Info("TargetedScan: item was created by concurrent request ({0}), scheduling ValidateChildren in background", existing.InternalId);
                var parentFolder = existing.GetParent() as Folder ?? knownAncestor;
                Task.Run(async () =>
                {
                    var vLock = _validateLocks.GetOrAdd(parentFolder.Path, _ => new SemaphoreSlim(1, 1));
                    await vLock.WaitAsync();
                    try
                    {
                        await parentFolder.ValidateChildren(
                            new Progress<double>(),
                            CancellationToken.None,
                            new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
                            {
                                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                                ReplaceAllMetadata = false
                            },
                            recursive: false);
                        _logger.Info("TargetedScan: background ValidateChildren completed on {0}", parentFolder.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("TargetedScan: background ValidateChildren failed on " + parentFolder.Name, ex);
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

            // Fire-and-forget ValidateChildren on the ancestor to properly register items
            // in parent cache and trigger metadata refresh. Runs in background so the API
            // response returns immediately (ValidateChildren can take 30+ seconds).
            _logger.Info("TargetedScan: scheduling ValidateChildren on {0} in background", knownAncestor.Name);
            Task.Run(async () =>
            {
                var vLock = _validateLocks.GetOrAdd(knownAncestor.Path, _ => new SemaphoreSlim(1, 1));
                await vLock.WaitAsync();
                try
                {
                    await knownAncestor.ValidateChildren(
                        new Progress<double>(),
                        CancellationToken.None,
                        new MetadataRefreshOptions(directoryService)
                        {
                            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                            ReplaceAllMetadata = true
                        },
                        recursive: true);
                    _logger.Info("TargetedScan: background ValidateChildren completed on {0}", knownAncestor.Name);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("TargetedScan: background ValidateChildren failed on " + knownAncestor.Name, ex);
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
        Discovered
    }
}
