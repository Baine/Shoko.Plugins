using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Video.Services;

namespace Shoko.Plugin.NfoGenerator;

/// <summary>
/// On-demand NFO generation triggers. All write to the media folders, so they
/// require admin credentials.
/// </summary>
[ApiController]
[ApiVersion("1")]
[Route("/api/plugin/NfoGenerator")]
[Authorize(Policy = "admin")]
public sealed class NfoGeneratorController : ControllerBase
{
    private readonly NfoGeneratorService _service;
    private readonly IMetadataService _metadataService;
    private readonly IVideoService _videoService;

    public NfoGeneratorController(NfoGeneratorService service, IMetadataService metadataService, IVideoService videoService)
    {
        _service = service;
        _metadataService = metadataService;
        _videoService = videoService;
    }

    /// <summary>Regenerates all NFO files for a series.</summary>
    [HttpPost("series/{seriesID}")]
    public IActionResult GenerateSeries(int seriesID)
    {
        if (_metadataService.GetShokoSeriesByID(seriesID) is not { } series)
            return NotFound(new { status = "error", message = $"Series {seriesID} not found" });
        return Ok(new { status = "ok", generated = _service.GenerateForSeries(series) });
    }

    /// <summary>Regenerates all NFO files for an episode.</summary>
    [HttpPost("episode/{episodeID}")]
    public IActionResult GenerateEpisode(int episodeID)
    {
        if (_metadataService.GetShokoEpisodeByID(episodeID) is not { } episode)
            return NotFound(new { status = "error", message = $"Episode {episodeID} not found" });
        return Ok(new { status = "ok", generated = _service.GenerateForEpisode(episode) });
    }

    /// <summary>Regenerates all NFO files inside an import folder.</summary>
    [HttpPost("folder/{folderID}")]
    public IActionResult GenerateFolder(int folderID)
    {
        if (_videoService.GetManagedFolderByID(folderID) is not { } folder)
            return NotFound(new { status = "error", message = $"Folder {folderID} not found" });
        return Ok(new { status = "ok", generated = _service.GenerateForFolder(folder) });
    }

    /// <summary>Regenerates NFO files for the entire library.</summary>
    [HttpPost("library")]
    public IActionResult GenerateLibrary() =>
        Ok(new { status = "ok", generated = _service.GenerateForLibrary() });
}
