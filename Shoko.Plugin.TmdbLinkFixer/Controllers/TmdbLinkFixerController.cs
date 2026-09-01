using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.User.Services;
using Shoko.Plugin.TmdbLinkFixer.Configuration;
using Shoko.Plugin.TmdbLinkFixer.Models;
using Shoko.Plugin.TmdbLinkFixer.Services;

namespace Shoko.Plugin.TmdbLinkFixer.Controllers;

[ApiController]
[Authorize]
[Route("api/plugin/tmdb-link-fixer")]
public sealed class TmdbLinkFixerController(TmdbLinkFixerService service, IUserService userService) : ControllerBase
{
    [HttpGet("page")]
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public IActionResult Page()
    {
        const string resource = "Shoko.Plugin.TmdbLinkFixer.Web.index.html";
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        return stream is null ? Problem($"Embedded resource '{resource}' was not found.") : File(stream, "text/html; charset=utf-8");
    }

    [HttpGet("links")]
    public ActionResult<IReadOnlyList<TmdbLinkItem>> Links()
    {
        if (!IsAdmin()) return Forbid();
        if (!service.TryGetLinks(out var links))
        {
            Response.Headers.RetryAfter = "2";
            return Accepted();
        }
        return Ok(links);
    }

    [HttpGet("scan")]
    public ActionResult<ScanState> ScanState()
        => IsAdmin() ? Ok(service.GetScanState()) : Forbid();

    [HttpPost("scan")]
    public IActionResult StartScan([FromQuery] bool ignoreCache = false)
    {
        if (!IsAdmin()) return Forbid();
        if (!service.ApiCredentialConfigured)
            return Conflict("Configure a TMDB API key or read access token before scanning.");
        return service.StartScan(ignoreCache) ? Accepted(service.GetScanState()) : Conflict("A scan is already running.");
    }

    [HttpGet("settings")]
    public ActionResult<TmdbLinkFixerSettingsView> Settings()
        => IsAdmin() ? Ok(TmdbLinkFixerSettingsStore.GetView()) : Forbid();

    [HttpPost("settings")]
    public ActionResult<TmdbLinkFixerSettingsView> UpdateSettings([FromBody] UpdateTmdbLinkFixerSettingsRequest request)
    {
        if (!IsAdmin()) return Forbid();
        return Ok(TmdbLinkFixerSettingsStore.Update(request));
    }

    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<SearchResult>>> Search([FromQuery] string query, CancellationToken cancellationToken)
    {
        if (!IsAdmin()) return Forbid();
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            return BadRequest("Enter at least two search characters.");
        return Ok(await service.SearchAsync(query, cancellationToken));
    }

    [HttpGet("suggestions")]
    public async Task<ActionResult<IReadOnlyList<SearchResult>>> Suggestions([FromQuery] string key, CancellationToken cancellationToken)
    {
        if (!IsAdmin()) return Forbid();
        if (string.IsNullOrWhiteSpace(key)) return BadRequest("A link key is required.");
        return Ok(await service.FindSuggestionsAsync(key, cancellationToken));
    }

    [HttpGet("show-mapping")]
    public async Task<ActionResult<ShowMappingOptions>> ShowMapping(
        [FromQuery] string key,
        [FromQuery] int targetId,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin()) return Forbid();
        if (string.IsNullOrWhiteSpace(key)) return BadRequest("A link key is required.");
        if (targetId <= 0) return BadRequest("A valid TMDB show ID is required.");
        var result = await service.GetShowMappingOptionsAsync(key, targetId, cancellationToken);
        return result is null ? NotFound("The source link or TMDB show could not be loaded.") : Ok(result);
    }

    [HttpPost("accept")]
    public async Task<ActionResult<OperationResult>> Accept([FromBody] AcceptLinkRequest request, CancellationToken cancellationToken)
    {
        if (!IsAdmin()) return Forbid();
        var result = await service.AcceptAsync(request, cancellationToken);
        return result.Success ? Ok(result) : Conflict(result);
    }

    private bool IsAdmin() => userService.GetUserFromHttpContext(HttpContext)?.IsAdmin == true;
}
