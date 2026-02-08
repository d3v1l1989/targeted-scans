using System;
using System.Collections.Generic;
using System.Linq;
using EmbyTargetedScan.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace EmbyTargetedScan.Api
{
    [Route("/Library/ScanPath", "POST", Summary = "Performs a targeted library scan for a specific path")]
    [Authenticated]
    public class ScanPathRequest : IReturn<ScanPathResponse>
    {
        public string Path { get; set; }
        public string LibraryId { get; set; }
    }

    [Route("/Library/ScanPaths", "POST", Summary = "Performs a targeted library scan for multiple paths in a single batch")]
    [Authenticated]
    public class ScanPathsRequest : IReturn<ScanPathsResponse>
    {
        public List<string> Paths { get; set; }
    }

    public class ScanPathResponse
    {
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string Status { get; set; }
        public string Path { get; set; }
        public string Message { get; set; }
    }

    public class ScanPathsResponse
    {
        public List<ScanPathResponse> Results { get; set; }
    }

    public class ScanPathService : IService, IRequiresRequest
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;

        public IRequest Request { get; set; }

        public ScanPathService(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _logger = logManager.GetLogger("TargetedScan");
        }

        public ScanPathResponse Post(ScanPathRequest request)
        {
            _logger.Info("ScanPath request for: {0}", request.Path);

            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return new ScanPathResponse
                {
                    Status = "BadRequest",
                    Message = "Path is required"
                };
            }

            var scanService = new TargetedScanService(_libraryManager, _providerManager, _fileSystem, _logger);
            var result = scanService.ScanPath(request.Path);

            return MapResult(result);
        }

        public ScanPathsResponse Post(ScanPathsRequest request)
        {
            if (request.Paths == null || request.Paths.Count == 0)
            {
                return new ScanPathsResponse { Results = new List<ScanPathResponse>() };
            }

            _logger.Info("ScanPaths batch request for {0} paths", request.Paths.Count);

            var scanService = new TargetedScanService(_libraryManager, _providerManager, _fileSystem, _logger);
            var results = scanService.ScanPaths(request.Paths);

            return new ScanPathsResponse
            {
                Results = results.Select(r => new ScanPathResponse
                {
                    ItemId = r.ItemId ?? string.Empty,
                    ItemName = r.ItemName ?? string.Empty,
                    Status = r.Status.ToString(),
                    Path = r.Path ?? string.Empty,
                    Message = r.Path ?? string.Empty
                }).ToList()
            };
        }

        private static ScanPathResponse MapResult(ScanPathResult result)
        {
            switch (result.Status)
            {
                case ScanStatus.Created:
                    return new ScanPathResponse
                    {
                        ItemId = result.ItemId ?? string.Empty,
                        ItemName = result.ItemName ?? string.Empty,
                        Status = "Created",
                        Message = "Item created and metadata refresh queued"
                    };
                case ScanStatus.Refreshed:
                    return new ScanPathResponse
                    {
                        ItemId = result.ItemId ?? string.Empty,
                        ItemName = result.ItemName ?? string.Empty,
                        Status = "Refreshed",
                        Message = "Existing item found, metadata refresh queued"
                    };
                case ScanStatus.PathNotFound:
                    return new ScanPathResponse
                    {
                        Status = "PathNotFound",
                        Message = "Path does not exist on filesystem"
                    };
                case ScanStatus.ParentNotFound:
                    return new ScanPathResponse
                    {
                        Status = "ParentNotFound",
                        Message = "Could not find parent library item for path"
                    };
                default:
                    return new ScanPathResponse
                    {
                        Status = "Failed",
                        Message = "Failed to scan path"
                    };
            }
        }
    }
}
