using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using JellyfinTargetedScan.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellyfinTargetedScan.Controllers;

/// <summary>
/// API controller for targeted library scanning.
/// </summary>
[ApiController]
[Route("Library")]
[Authorize]
public class ScanPathController : ControllerBase
{
    private readonly TargetedScanService _scanService;
    private readonly ILogger<ScanPathController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanPathController"/> class.
    /// </summary>
    public ScanPathController(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<ScanPathController> logger,
        ILogger<TargetedScanService> scanServiceLogger)
    {
        _scanService = new TargetedScanService(libraryManager, providerManager, fileSystem, scanServiceLogger);
        _logger = logger;
    }

    /// <summary>
    /// Scan a specific path to discover new content without triggering a full library scan.
    /// </summary>
    /// <param name="request">The scan path request.</param>
    /// <returns>Scan result with item information.</returns>
    [HttpPost("ScanPath")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ScanPathResponse>> PostScanPath([FromBody, Required] ScanPathRequest request)
    {
        _logger.LogInformation("ScanPath request for: {Path}", request.Path);

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest(new ScanPathResponse
            {
                Status = "BadRequest",
                Message = "Path is required"
            });
        }

        var result = await _scanService.ScanPathAsync(request.Path).ConfigureAwait(false);

        return result.Status switch
        {
            ScanStatus.Created => Ok(new ScanPathResponse
            {
                ItemId = result.ItemId ?? string.Empty,
                ItemName = result.ItemName ?? string.Empty,
                Status = "Created",
                Message = "Item created and metadata refresh queued"
            }),
            ScanStatus.Refreshed => Ok(new ScanPathResponse
            {
                ItemId = result.ItemId ?? string.Empty,
                ItemName = result.ItemName ?? string.Empty,
                Status = "Refreshed",
                Message = "Existing item found, metadata refresh queued"
            }),
            ScanStatus.PathNotFound => NotFound(new ScanPathResponse
            {
                Status = "PathNotFound",
                Message = $"Path does not exist on filesystem: {request.Path}"
            }),
            ScanStatus.ParentNotFound => NotFound(new ScanPathResponse
            {
                Status = "ParentNotFound",
                Message = $"Could not find parent library item for path: {request.Path}"
            }),
            _ => StatusCode(StatusCodes.Status500InternalServerError, new ScanPathResponse
            {
                Status = "Failed",
                Message = $"Failed to scan path: {request.Path}"
            })
        };
    }

    /// <summary>
    /// Scan multiple paths in a single batch request. Paths are processed shallowest-first
    /// so parent items (Series, Season) are created before children (Episode).
    /// </summary>
    /// <param name="request">The batch scan request.</param>
    /// <returns>Results for each path.</returns>
    [HttpPost("ScanPaths")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ScanPathsResponse>> PostScanPaths([FromBody, Required] ScanPathsRequest request)
    {
        if (request.Paths == null || request.Paths.Count == 0)
        {
            return BadRequest(new ScanPathsResponse
            {
                Results = new List<ScanPathResponse>()
            });
        }

        _logger.LogInformation("ScanPaths batch request for {Count} paths", request.Paths.Count);

        var results = await _scanService.ScanPathsAsync(request.Paths).ConfigureAwait(false);

        return Ok(new ScanPathsResponse
        {
            Results = results.Select(r => new ScanPathResponse
            {
                ItemId = r.ItemId ?? string.Empty,
                ItemName = r.ItemName ?? string.Empty,
                Status = r.Status.ToString(),
                Path = r.Path ?? string.Empty,
                Message = r.Path ?? string.Empty
            }).ToList()
        });
    }
}

/// <summary>
/// Request body for POST /Library/ScanPath.
/// </summary>
public class ScanPathRequest
{
    /// <summary>
    /// Gets or sets the filesystem path to scan.
    /// </summary>
    [Required]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional library ID to scope the scan.
    /// </summary>
    public string? LibraryId { get; set; }
}

/// <summary>
/// Request body for POST /Library/ScanPaths.
/// </summary>
public class ScanPathsRequest
{
    /// <summary>
    /// Gets or sets the list of filesystem paths to scan.
    /// </summary>
    [Required]
    public List<string> Paths { get; set; } = new();
}

/// <summary>
/// Response body for POST /Library/ScanPath.
/// </summary>
public class ScanPathResponse
{
    /// <summary>
    /// Gets or sets the item ID.
    /// </summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item name.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the status.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the scanned path (populated in batch responses).
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response body for POST /Library/ScanPaths.
/// </summary>
public class ScanPathsResponse
{
    /// <summary>
    /// Gets or sets the list of results.
    /// </summary>
    public List<ScanPathResponse> Results { get; set; } = new();
}
