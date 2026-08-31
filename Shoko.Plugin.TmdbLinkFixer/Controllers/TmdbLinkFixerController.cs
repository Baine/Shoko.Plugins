using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.User.Services;
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
    public IActionResult StartScan()
    {
        if (!IsAdmin()) return Forbid();
        return service.StartScan() ? Accepted(service.GetScanState()) : Conflict("A scan is already running.");
    }

    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<SearchResult>>> Search([FromQuery] string query, CancellationToken cancellationToken)
    {
        if (!IsAdmin()) return Forbid();
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            return BadRequest("Mindestens zwei Suchzeichen sind erforderlich.");
        return Ok(await service.SearchAsync(query, cancellationToken));
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
