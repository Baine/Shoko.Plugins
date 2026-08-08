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

    /// <summary>
    /// Serves the settings page shown by the WebUI under Settings → Plugins.
    /// The page itself is scaffolding only; every data call it makes is
    /// authenticated with the user's apikey.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("settings")]
    public IActionResult GetSettingsPage() => Content(SettingsPageHtml, "text/html");

    private const string SettingsPageHtml = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8" />
        <title>NFO Generator</title>
        <style>
          :root { color-scheme: dark; }
          body { font-family: system-ui, -apple-system, sans-serif; margin: 0; padding: 24px; background: #1b1f27; color: #cfd8e3; }
          h1 { font-size: 18px; margin: 0 0 4px; }
          .sub { color: #8b93a1; font-size: 13px; margin-bottom: 20px; }
          .field { display: block; margin-bottom: 14px; font-size: 13px; }
          .field > span { display: block; margin-bottom: 4px; color: #aab2c0; }
          .field small { display: block; color: #6d7583; margin-top: 4px; }
          input[type="text"] { width: 100%; box-sizing: border-box; padding: 8px 10px; border: 1px solid #2c333e; border-radius: 6px; background: #14171d; color: #cfd8e3; }
          .row { display: flex; gap: 24px; flex-wrap: wrap; }
          .row .field { display: flex; align-items: center; gap: 8px; }
          .row .field > span { margin: 0; }
          button { padding: 8px 14px; border: none; border-radius: 6px; background: #44a3ff; color: #fff; font-weight: 600; cursor: pointer; }
          button.secondary { background: #2c333e; }
          button:disabled { opacity: .5; cursor: default; }
          #status { margin-top: 16px; font-size: 13px; min-height: 18px; white-space: pre-wrap; }
          #status.error { color: #ff6c6c; }
          #status.ok { color: #10c469; }
          hr { border: none; border-top: 1px solid #2c333e; margin: 20px 0; }
        </style>
        </head>
        <body>
          <h1>NFO Generator</h1>
          <div class="sub">Writes Kodi-style NFO files and artwork sidecars next to your video files.</div>

          <form id="config-form">
            <label class="field">
              <span>Title Language</span>
              <input type="text" name="TitleLanguage" spellcheck="false" />
              <small>Priority, comma separated: language codes (de-DE, en-US, ja-JP, x-jat), 'shoko' for Shoko's preferred title, 'original' for the source default.</small>
            </label>
            <label class="field">
              <span>Description Language</span>
              <input type="text" name="DescriptionLanguage" spellcheck="false" />
              <small>Priority, comma separated. Same tokens as Title Language.</small>
            </label>
            <div class="row">
              <label class="field">
                <input type="checkbox" name="GenerateOnImport" />
                <span>Generate On Import</span>
              </label>
              <label class="field">
                <input type="checkbox" name="GenerateOnMetadataUpdate" />
                <span>Generate On Metadata Update</span>
              </label>
            </div>
            <button type="submit">Save settings</button>
          </form>

          <hr />

          <button type="button" id="regenerate" class="secondary">Regenerate library</button>

          <div id="status"></div>

          <script>
            const pluginID = '5c5482c1-3dd0-49cb-b862-d57e305da353';
            const statusEl = document.getElementById('status');
            const form = document.getElementById('config-form');

            function getApikey() {
              try {
                const ss = JSON.parse(sessionStorage.getItem('state') || '{}');
                if (ss.apiSession?.apikey) return ss.apiSession.apikey;
              } catch (e) { /* ignore */ }
              try {
                const ls = JSON.parse(localStorage.getItem('apiSession') || '{}');
                if (ls.apikey) return ls.apikey;
              } catch (e) { /* ignore */ }
              return '';
            }

            const headers = () => ({ apikey: getApikey(), 'Content-Type': 'application/json' });

            function setStatus(text, kind) {
              statusEl.textContent = text;
              statusEl.className = kind || '';
            }

            let configID = '';

            async function loadConfig() {
              const listRes = await fetch(`/api/v3/Configuration?pluginID=${pluginID}`, { headers: headers() });
              if (!listRes.ok) throw new Error(`Failed to list configurations (${listRes.status}).`);
              const list = await listRes.json();
              const info = Array.isArray(list) ? list[0] : null;
              if (!info) throw new Error('NFO Generator configuration not found. Is the plugin active?');
              configID = info.ID;

              const res = await fetch(`/api/v3/Configuration/${configID}`, { headers: headers() });
              if (!res.ok) throw new Error(`Failed to load settings (${res.status}).`);
              const cfg = await res.json();
              form.elements.TitleLanguage.value = cfg.TitleLanguage || 'shoko';
              form.elements.DescriptionLanguage.value = cfg.DescriptionLanguage || 'shoko';
              form.elements.GenerateOnImport.checked = !!cfg.GenerateOnImport;
              form.elements.GenerateOnMetadataUpdate.checked = !!cfg.GenerateOnMetadataUpdate;
            }

            form.addEventListener('submit', async (event) => {
              event.preventDefault();
              if (!configID) return setStatus('Settings not loaded yet.', 'error');
              setStatus('Saving...');
              const body = {
                TitleLanguage: form.elements.TitleLanguage.value.trim(),
                DescriptionLanguage: form.elements.DescriptionLanguage.value.trim(),
                GenerateOnImport: form.elements.GenerateOnImport.checked,
                GenerateOnMetadataUpdate: form.elements.GenerateOnMetadataUpdate.checked,
              };
              const res = await fetch(`/api/v3/Configuration/${configID}`, { method: 'PUT', headers: headers(), body: JSON.stringify(body) });
              if (!res.ok) return setStatus(`Save failed (${res.status}).`, 'error');
              setStatus('Settings saved.', 'ok');
            });

            document.getElementById('regenerate').addEventListener('click', async () => {
              setStatus('Regenerating library...');
              const res = await fetch('/api/plugin/NfoGenerator/library', { method: 'POST', headers: headers() });
              if (!res.ok) return setStatus(`Regenerate failed (${res.status}).`, 'error');
              const data = await res.json();
              setStatus(`Done — ${data.generated} NFO file(s) written.`, 'ok');
            });

            loadConfig().catch(err => setStatus(err.message, 'error'));
          </script>
        </body>
        </html>
        """;
}
