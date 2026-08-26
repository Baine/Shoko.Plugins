using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Logging;

namespace Shoko.Plugin.MovieMissingFilter.Configuration;

internal static class SettingsDashboardMiddleware
{
    internal const string DashboardPath = "/api/plugin/MovieMissingFilter/dashboard";

    internal static void Register(IApplicationBuilder application, ILogger logger)
    {
        application.Use(async (context, next) =>
        {
            if (!context.Request.Path.Equals(DashboardPath, StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method))
            {
                await HandlePost(context, logger);
                return;
            }

            if (HttpMethods.IsGet(context.Request.Method))
            {
                await Render(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        });
    }

    private static async Task HandlePost(HttpContext context, ILogger logger)
    {
        var form = await context.Request.ReadFormAsync();
        var settings = new MovieMissingFilterSettings
        {
            IncludeNormalEpisodes = IsChecked(form["normal"]),
            IncludeSpecials = IsChecked(form["specials"]),
            IncludeOthers = IsChecked(form["others"]),
        };

        MovieMissingFilterSettingsStore.Update(settings);
        logger.LogInformation("[MovieMissingFilter] Settings saved from plugin dashboard.");

        var query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty;
        context.Response.Redirect(DashboardPath + query);
    }

    private static bool IsChecked(StringValues value)
        => value.Any(item => string.Equals(item, "on", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item, "1", StringComparison.OrdinalIgnoreCase));

    private static async Task Render(HttpContext context)
    {
        var settings = MovieMissingFilterSettingsStore.Current;
        var path = WebUtility.HtmlEncode(MovieMissingFilterSettingsStore.SettingsPath);
        var html = BuildHtml(settings, path);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        await context.Response.WriteAsync(html, Encoding.UTF8);
    }

    private static string Checked(bool value) => value ? " checked" : string.Empty;

    private static string BuildHtml(MovieMissingFilterSettings settings, string settingsPath)
        => $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Movie Missing Filter</title>
<style>
:root { color-scheme: dark light; font-family: system-ui,-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif; }
body { margin:0; padding:24px; background:#111827; color:#e5e7eb; }
main { max-width:760px; margin:0 auto; }
h1 { margin:0 0 8px; font-size:26px; }
p { color:#b8c0cc; line-height:1.5; }
.card { margin-top:20px; padding:20px; border:1px solid #374151; border-radius:12px; background:#1f2937; }
.option { display:flex; gap:14px; align-items:flex-start; padding:14px 0; border-bottom:1px solid #374151; }
.option:last-of-type { border-bottom:0; }
input[type=checkbox] { width:20px; height:20px; margin-top:2px; }
label { font-weight:650; }
.small { display:block; margin-top:4px; color:#9ca3af; font-size:14px; font-weight:400; }
button { margin-top:18px; border:0; border-radius:8px; padding:10px 18px; font-weight:700; cursor:pointer; background:#2563eb; color:white; }
code { word-break:break-all; color:#cbd5e1; }
.note { margin-top:18px; padding:12px 14px; border-radius:8px; background:#111827; color:#cbd5e1; font-size:14px; }
@media (prefers-color-scheme: light) {
 body { background:#f3f4f6; color:#111827; }
 .card { background:white; border-color:#d1d5db; }
 .option { border-color:#e5e7eb; }
 p,.small { color:#4b5563; }
 .note { background:#f3f4f6; color:#374151; }
 code { color:#374151; }
}
</style>
</head>
<body>
<main>
<h1>Movie Missing Filter</h1>
<p>Select which AniDB episode types should be visible and counted as missing. Changes are used on the next API request; refresh the Missing Episodes page and Dashboard after saving.</p>
<form method="post">
<div class="card">
<div class="option">
<input id="normal" name="normal" type="checkbox"{{Checked(settings.IncludeNormalEpisodes)}}>
<div><label for="normal">Normal episodes (E)</label><span class="small">Shoko's normal episode entries. Movie Complete Movie / Part X of Y suppression is applied only to this type.</span></div>
</div>
<div class="option">
<input id="specials" name="specials" type="checkbox"{{Checked(settings.IncludeSpecials)}}>
<div><label for="specials">Specials (S)</label><span class="small">Include aired, non-hidden Specials without a local file.</span></div>
</div>
<div class="option">
<input id="others" name="others" type="checkbox"{{Checked(settings.IncludeOthers)}}>
<div><label for="others">Other (O)</label><span class="small">Include aired, non-hidden AniDB Other episodes without a local file.</span></div>
</div>
<button type="submit">Save settings</button>
<div class="note">All combinations are supported, including only E, only S, only O, S+O, or all three. If all three are disabled the Missing Episodes result becomes empty. Settings file: <code>{{settingsPath}}</code></div>
</div>
</form>
</main>
</body>
</html>
""";
}
