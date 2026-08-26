using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Shoko.Abstractions.User.Services;
using Shoko.Plugin.AniDBWatchHistory.Models;
using Shoko.Plugin.AniDBWatchHistory.Services;

namespace Shoko.Plugin.AniDBWatchHistory.Controllers;

[ApiController]
[Authorize]
[Route("api/plugin/anidb-watch-history")]
public sealed class AniDBWatchHistoryController(
    AniDBWatchImporter importer,
    IUserService userService) : ControllerBase
{
    [HttpGet("page")]
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public IActionResult Page()
    {
        const string resourceName = "Shoko.Plugin.AniDBWatchHistory.Web.index.html";
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        return stream is null
            ? Problem($"Embedded resource '{resourceName}' was not found.")
            : File(stream, "text/html; charset=utf-8");
    }

    [HttpGet("anidb-user")]
    [ProducesResponseType<ShokoUserDto>(StatusCodes.Status200OK)]
    public ActionResult<ShokoUserDto> GetAniDBUser()
    {
        if (!IsAdmin()) return Forbid();
        try { return Ok(importer.GetAniDBUser()); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [HttpPost("analyze")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(512L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 512L * 1024 * 1024)]
    public Task<ActionResult<ImportResult>> Analyze(
        [FromForm] ImportForm form, CancellationToken cancellationToken)
        => Run(form, dryRun: true, cancellationToken);

    [HttpPost("import")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(512L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 512L * 1024 * 1024)]
    public Task<ActionResult<ImportResult>> Import(
        [FromForm] ImportForm form, CancellationToken cancellationToken)
        => Run(form, dryRun: false, cancellationToken);

    private async Task<ActionResult<ImportResult>> Run(ImportForm form, bool dryRun, CancellationToken cancellationToken)
    {
        if (!IsAdmin()) return Forbid();
        if (form.XmlFile is null || form.XmlFile.Length == 0)
            return BadRequest("XmlFile is required.");
        if (!form.XmlFile.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return BadRequest("XmlFile must be an .xml file.");

        try
        {
            await using var stream = form.XmlFile.OpenReadStream();
            return Ok(await importer.ImportAsync(
                stream,
                dryRun,
                form.VerifyEpisodeId,
                form.AllowEpisodeIdFallback,
                cancellationToken));
        }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
        catch (System.Xml.XmlException ex) { return BadRequest($"Invalid AniDB XML: {ex.Message}"); }
    }

    private bool IsAdmin() => userService.GetUserFromHttpContext(HttpContext)?.IsAdmin == true;
}

public sealed class ImportForm
{
    public required IFormFile XmlFile { get; init; }
    public bool VerifyEpisodeId { get; init; } = true;
    public bool AllowEpisodeIdFallback { get; init; }
}
