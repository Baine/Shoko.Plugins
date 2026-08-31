using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shoko.Plugin.TmdbLinkFixer.Configuration;
using Shoko.Plugin.TmdbLinkFixer.Models;

namespace Shoko.Plugin.TmdbLinkFixer.Services;

public sealed class TmdbLinkProbe(IHttpClientFactory clientFactory, ILogger<TmdbLinkProbe> logger)
{
    internal const string HttpClientName = "Shoko.Plugin.TmdbLinkFixer.Api";
    private readonly SemaphoreSlim _rateGate = new(1, 1);
    private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

    public async Task<ProbeResult> ProbeAsync(TmdbMediaKind expectedKind, int expectedId, CancellationToken cancellationToken = default)
    {
        if (expectedId <= 0)
            return ProbeResult.Invalid("The TMDB ID is invalid.");

        var settings = TmdbLinkFixerSettingsStore.Current;
        if (string.IsNullOrWhiteSpace(settings.ApiCredential))
            return ProbeResult.Error("Configure a TMDB API key or read access token before scanning.", fatal: true);

        var expected = await RequestEntityAsync(expectedKind, expectedId, settings, cancellationToken).ConfigureAwait(false);
        if (expected.State == ApiEntityState.Exists)
            return ProbeResult.Valid();
        if (expected.State == ApiEntityState.Error)
            return ProbeResult.Error(expected.ErrorMessage!, expected.Fatal);

        // TMDB movie and TV namespaces can contain the same numeric ID. We only
        // present the alternate type as a candidate when the expected endpoint
        // is missing and the alternate endpoint actually exists.
        var alternateKind = expectedKind == TmdbMediaKind.Movie ? TmdbMediaKind.Show : TmdbMediaKind.Movie;
        var alternate = await RequestEntityAsync(alternateKind, expectedId, settings, cancellationToken).ConfigureAwait(false);
        if (alternate.State == ApiEntityState.Exists)
            return ProbeResult.Redirected(
                alternateKind,
                expectedId,
                alternate.PosterUrl,
                $"The TMDB {KindLabel(expectedKind)} endpoint is missing, but {KindLabel(alternateKind)} {expectedId} exists. Review it; matching numeric IDs can still refer to unrelated titles.");
        if (alternate.State == ApiEntityState.Error)
            return ProbeResult.Error($"The original link is missing, but the alternate media type could not be checked: {alternate.ErrorMessage}", alternate.Fatal);

        return ProbeResult.Invalid("TMDB reports that this entry does not exist.");
    }

    public static Uri BuildUri(TmdbMediaKind kind, int id)
        => new($"https://www.themoviedb.org/{(kind == TmdbMediaKind.Movie ? "movie" : "tv")}/{id}");

    private async Task<ApiEntityResult> RequestEntityAsync(
        TmdbMediaKind kind,
        int id,
        TmdbLinkFixerSettings settings,
        CancellationToken cancellationToken)
    {
        await WaitForRateSlotAsync(settings.RequestsPerSecond, cancellationToken).ConfigureAwait(false);
        var route = $"{(kind == TmdbMediaKind.Movie ? "movie" : "tv")}/{id}?language=en-US";
        if (!TmdbLinkFixerSettingsStore.IsBearerToken(settings.ApiCredential))
            route += $"&api_key={Uri.EscapeDataString(settings.ApiCredential)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            if (TmdbLinkFixerSettingsStore.IsBearerToken(settings.ApiCredential))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiCredential);
            using var response = await clientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return ApiEntityResult.Missing();
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return ApiEntityResult.Error("TMDB rejected the configured API credential.", fatal: true);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                if (retry < TimeSpan.FromSeconds(1)) retry = TimeSpan.FromSeconds(1);
                if (retry > TimeSpan.FromMinutes(5)) retry = TimeSpan.FromMinutes(5);
                logger.LogWarning("TMDB returned 429; pausing validation requests for {Delay}", retry);
                await PauseAllRequestsAsync(retry, cancellationToken).ConfigureAwait(false);
                return ApiEntityResult.Error($"TMDB rate-limited the request. Validation paused for {Math.Ceiling(retry.TotalSeconds)} seconds; lower the configured request rate if this repeats.");
            }
            if ((int)response.StatusCode >= 500)
                return ApiEntityResult.Error($"TMDB is temporarily unavailable (HTTP {(int)response.StatusCode}).");
            if (!response.IsSuccessStatusCode)
                return ApiEntityResult.Error($"TMDB API validation failed (HTTP {(int)response.StatusCode}).");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var posterPath = root.TryGetProperty("poster_path", out var poster) && poster.ValueKind == JsonValueKind.String
                ? poster.GetString()
                : null;
            return ApiEntityResult.Exists(Poster(posterPath));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiEntityResult.Error("The TMDB API request timed out.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "TMDB API probe failed for {Kind} {TmdbId}", kind, id);
            return ApiEntityResult.Error("The TMDB API could not be reached.");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "TMDB API returned invalid JSON for {Kind} {TmdbId}", kind, id);
            return ApiEntityResult.Error("TMDB returned an unreadable response.");
        }
    }

    private async Task WaitForRateSlotAsync(int requestsPerSecond, CancellationToken cancellationToken)
    {
        await _rateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var delay = _nextRequestAt - now;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            var interval = TimeSpan.FromSeconds(1d / Math.Clamp(requestsPerSecond, 1, 10));
            _nextRequestAt = DateTimeOffset.UtcNow + interval;
        }
        finally
        {
            _rateGate.Release();
        }
    }

    private async Task PauseAllRequestsAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var blockedUntil = DateTimeOffset.UtcNow + delay;
        await _rateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_nextRequestAt < blockedUntil)
                _nextRequestAt = blockedUntil;
        }
        finally
        {
            _rateGate.Release();
        }

        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private static string? Poster(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : $"https://image.tmdb.org/t/p/w185{path}";

    private static string KindLabel(TmdbMediaKind kind) => kind == TmdbMediaKind.Movie ? "movie" : "show";

    private enum ApiEntityState { Exists, Missing, Error }

    private sealed record ApiEntityResult(ApiEntityState State, string? PosterUrl, string? ErrorMessage, bool Fatal)
    {
        public static ApiEntityResult Exists(string? posterUrl) => new(ApiEntityState.Exists, posterUrl, null, false);
        public static ApiEntityResult Missing() => new(ApiEntityState.Missing, null, null, false);
        public static ApiEntityResult Error(string message, bool fatal = false) => new(ApiEntityState.Error, null, message, fatal);
    }
}

public sealed record ProbeResult(LinkHealth Health, string? Message, TmdbMediaKind? RedirectKind, int? RedirectId, string? RedirectPosterUrl, bool Fatal)
{
    public static ProbeResult Valid() => new(LinkHealth.Valid, null, null, null, null, false);
    public static ProbeResult Invalid(string message) => new(LinkHealth.Invalid, message, null, null, null, false);
    public static ProbeResult Error(string message, bool fatal = false) => new(LinkHealth.Error, message, null, null, null, fatal);
    public static ProbeResult Redirected(TmdbMediaKind kind, int id, string? posterUrl, string message) => new(LinkHealth.Redirected, message, kind, id, posterUrl, false);
}
