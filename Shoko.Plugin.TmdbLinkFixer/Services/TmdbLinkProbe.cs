using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Shoko.Plugin.TmdbLinkFixer.Models;

namespace Shoko.Plugin.TmdbLinkFixer.Services;

public sealed class TmdbLinkProbe(IHttpClientFactory clientFactory, ILogger<TmdbLinkProbe> logger)
{
    internal const string HttpClientName = "Shoko.Plugin.TmdbLinkFixer.Probe";
    private static readonly Regex OpenGraphImageRegex = new(
        "<meta[^>]+property=[\\\"']og:image[\\\"'][^>]+content=[\\\"'](?<url>[^\\\"']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<ProbeResult> ProbeAsync(TmdbMediaKind expectedKind, int expectedId, CancellationToken cancellationToken = default)
    {
        if (expectedId <= 0)
            return ProbeResult.Invalid("The TMDB ID is invalid.");

        var current = BuildUri(expectedKind, expectedId);
        var client = clientFactory.CreateClient(HttpClientName);

        try
        {
            for (var redirect = 0; redirect <= 5; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    if (response.Headers.Location is null)
                        return ProbeResult.Error($"TMDB returned HTTP {(int)response.StatusCode} without a redirect target.");
                    current = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current, response.Headers.Location);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
                    return ProbeResult.Invalid("TMDB reports that this entry does not exist.");

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    return ProbeResult.Error("TMDB is rate-limiting requests. Try the scan again later.");

                if ((int)response.StatusCode >= 500)
                    return ProbeResult.Error($"TMDB is temporarily unavailable (HTTP {(int)response.StatusCode}).");

                if (!response.IsSuccessStatusCode)
                    return ProbeResult.Error($"TMDB validation failed (HTTP {(int)response.StatusCode}).");

                if (!TryParseEntityUri(current, out var actualKind, out var actualId))
                    return ProbeResult.Error("TMDB returned an unexpected target URL.");

                if (actualKind == expectedKind && actualId == expectedId)
                    return ProbeResult.Valid();

                var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var posterUrl = ExtractPosterUrl(html);
                return ProbeResult.Redirected(actualKind, actualId, posterUrl, $"TMDB redirects this link to {KindLabel(actualKind)} {actualId}.");
            }

            return ProbeResult.Error("TMDB returned too many redirects.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Error("The TMDB validation request timed out.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "TMDB link probe failed for {Kind} {TmdbId}", expectedKind, expectedId);
            return ProbeResult.Error("TMDB could not be reached.");
        }
    }

    public static Uri BuildUri(TmdbMediaKind kind, int id)
        => new($"https://www.themoviedb.org/{(kind == TmdbMediaKind.Movie ? "movie" : "tv")}/{id}");

    internal static bool TryParseEntityUri(Uri uri, out TmdbMediaKind kind, out int id)
    {
        kind = default;
        id = 0;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;
        if (segments[0].Equals("movie", StringComparison.OrdinalIgnoreCase))
            kind = TmdbMediaKind.Movie;
        else if (segments[0].Equals("tv", StringComparison.OrdinalIgnoreCase))
            kind = TmdbMediaKind.Show;
        else
            return false;

        var idPart = segments[1].Split('-', 2)[0];
        return int.TryParse(idPart, out id) && id > 0;
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static string KindLabel(TmdbMediaKind kind) => kind == TmdbMediaKind.Movie ? "movie" : "show";

    private static string? ExtractPosterUrl(string html)
    {
        var match = OpenGraphImageRegex.Match(html);
        if (!match.Success)
            return null;
        var value = WebUtility.HtmlDecode(match.Groups["url"].Value);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
    }
}

public sealed record ProbeResult(LinkHealth Health, string? Message, TmdbMediaKind? RedirectKind, int? RedirectId, string? RedirectPosterUrl)
{
    public static ProbeResult Valid() => new(LinkHealth.Valid, null, null, null, null);
    public static ProbeResult Invalid(string message) => new(LinkHealth.Invalid, message, null, null, null);
    public static ProbeResult Error(string message) => new(LinkHealth.Error, message, null, null, null);
    public static ProbeResult Redirected(TmdbMediaKind kind, int id, string? posterUrl, string message) => new(LinkHealth.Redirected, message, kind, id, posterUrl);
}
