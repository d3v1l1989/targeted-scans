using System;
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
        private static readonly SemaphoreSlim _createLock = new SemaphoreSlim(1, 1);

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

        public ScanPathResult ScanPath(string path)
        {
            _logger.Info("TargetedScan: scanning path {0}", path);

            // 1. Verify path exists on filesystem
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                _logger.Warn("TargetedScan: path does not exist on filesystem: {0}", path);
                return new ScanPathResult { Status = ScanStatus.PathNotFound };
            }

            // 2. Check if item already exists in the database
            var existing = _libraryManager.FindByPath(path, null);
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

            // 3. Acquire lock to prevent races when creating items
            _createLock.Wait();
            try
            {
                return CreateItems(path);
            }
            finally
            {
                _createLock.Release();
            }
        }

        /// <summary>
        /// Scan multiple paths in a single batch. Sorted shallowest-first so
        /// ancestors are created before their children.
        /// Emby does not auto-discover siblings during metadata refresh,
        /// so every path must be scanned individually.
        /// </summary>
        public List<ScanPathResult> ScanPaths(IEnumerable<string> paths)
        {
            var sorted = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
                .ToList();

            _logger.Info("TargetedScan: batch scanning {0} paths", sorted.Count);

            var results = new List<ScanPathResult>();
            foreach (var path in sorted)
            {
                var result = ScanPath(path);
                result.Path = path;
                results.Add(result);
            }

            return results;
        }

        private ScanPathResult CreateItems(string path)
        {
            // Re-check after acquiring lock — another request may have created it
            var existing = _libraryManager.FindByPath(path, null);
            if (existing != null)
            {
                _logger.Info("TargetedScan: item was created by concurrent request ({0}), queuing refresh", existing.InternalId);
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

            // Walk up the directory tree to find the nearest known ancestor
            var missingPaths = new List<string> { path };
            Folder knownAncestor = null;
            var current = Path.GetDirectoryName(path);

            while (!string.IsNullOrEmpty(current))
            {
                var found = _libraryManager.FindByPath(current, null) as Folder;
                if (found != null)
                {
                    knownAncestor = found;
                    break;
                }

                missingPaths.Add(current);
                current = Path.GetDirectoryName(current);
            }

            if (knownAncestor == null)
            {
                _logger.Error("TargetedScan: could not find any ancestor in database for path: {0}", path);
                return new ScanPathResult { Status = ScanStatus.ParentNotFound };
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

                // Queue metadata refresh for every created item
                _providerManager.QueueRefresh(
                    newItem.InternalId,
                    new MetadataRefreshOptions(directoryService)
                    {
                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                        ReplaceAllMetadata = true
                    },
                    RefreshPriority.High);
                _logger.Info("TargetedScan: metadata refresh queued for {0}", newItem.Name);

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
