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

    public async Task<TmdbSearchResponse> SearchAsync(
        TmdbMediaKind kind,
        string query,
        int maximumResults = 8,
        CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length < 2)
            return TmdbSearchResponse.Success([]);

        var settings = TmdbLinkFixerSettingsStore.Current;
        if (string.IsNullOrWhiteSpace(settings.ApiCredential))
            return TmdbSearchResponse.Failure("Configure a TMDB API key or read access token before searching.");

        await WaitForRateSlotAsync(settings.RequestsPerSecond, cancellationToken).ConfigureAwait(false);
        var route = $"search/{(kind == TmdbMediaKind.Movie ? "movie" : "tv")}?query={Uri.EscapeDataString(query)}&include_adult=true&language=en-US&page=1";
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

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return TmdbSearchResponse.Failure("TMDB rejected the configured API credential.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = ClampRetryDelay(response.Headers.RetryAfter?.Delta);
                logger.LogWarning("TMDB returned 429; pausing search and validation requests for {Delay}", retry);
                await PauseAllRequestsAsync(retry, cancellationToken).ConfigureAwait(false);
                return TmdbSearchResponse.Failure($"TMDB rate-limited the search. Requests were paused for {Math.Ceiling(retry.TotalSeconds)} seconds.");
            }
            if ((int)response.StatusCode >= 500)
                return TmdbSearchResponse.Failure($"TMDB is temporarily unavailable (HTTP {(int)response.StatusCode}).");
            if (!response.IsSuccessStatusCode)
                return TmdbSearchResponse.Failure($"TMDB API search failed (HTTP {(int)response.StatusCode}).");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return TmdbSearchResponse.Failure("TMDB returned an unreadable search response.");

            var parsed = results.EnumerateArray()
                .Select(item => ParseSearchResult(kind, item))
                .OfType<SearchResult>()
                .Take(Math.Clamp(maximumResults, 1, 20))
                .ToList();
            return TmdbSearchResponse.Success(parsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TmdbSearchResponse.Failure("The TMDB API search timed out.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "TMDB API search failed for {Kind} query {Query}", kind, query);
            return TmdbSearchResponse.Failure("The TMDB API could not be reached.");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "TMDB API returned invalid search JSON for {Kind} query {Query}", kind, query);
            return TmdbSearchResponse.Failure("TMDB returned an unreadable search response.");
        }
    }

    public async Task<ShowMappingOptions?> GetShowMappingOptionsAsync(int showId, CancellationToken cancellationToken = default)
    {
        if (showId <= 0)
            return null;

        var settings = TmdbLinkFixerSettingsStore.Current;
        if (string.IsNullOrWhiteSpace(settings.ApiCredential))
            throw new InvalidOperationException("Configure a TMDB API key or read access token before loading episode mappings.");

        var showResponse = await RequestJsonAsync($"tv/{showId}?language=en-US", settings, "show episode mapping", cancellationToken).ConfigureAwait(false);
        if (showResponse.Element is null)
            throw new InvalidOperationException(showResponse.Error ?? "The TMDB show could not be loaded.");

        var show = showResponse.Element.Value;
        var showTitle = GetString(show, "name") ?? GetString(show, "original_name") ?? $"TMDB show {showId}";
        if (!show.TryGetProperty("seasons", out var seasons) || seasons.ValueKind != JsonValueKind.Array)
            return new(showId, showTitle, []);

        var episodes = new List<TmdbEpisodeOption>();
        foreach (var season in seasons.EnumerateArray().OrderBy(x => GetInt32(x, "season_number")))
        {
            var seasonNumber = GetInt32(season, "season_number");
            if (seasonNumber is null || GetInt32(season, "episode_count") is not > 0)
                continue;

            var seasonResponse = await RequestJsonAsync(
                $"tv/{showId}/season/{seasonNumber.Value}?language=en-US",
                settings,
                $"show season {seasonNumber.Value} episode mapping",
                cancellationToken).ConfigureAwait(false);
            if (seasonResponse.Element is null)
                throw new InvalidOperationException(seasonResponse.Error ?? $"TMDB season {seasonNumber.Value} could not be loaded.");
            if (!seasonResponse.Element.Value.TryGetProperty("episodes", out var seasonEpisodes) || seasonEpisodes.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var episode in seasonEpisodes.EnumerateArray())
            {
                var episodeId = GetInt32(episode, "id");
                var episodeNumber = GetInt32(episode, "episode_number");
                if (episodeId is null or <= 0 || episodeNumber is null or < 0)
                    continue;
                var title = GetString(episode, "name") ?? $"Episode {episodeNumber.Value}";
                DateOnly? airDate = DateOnly.TryParse(GetString(episode, "air_date"), out var parsedDate) ? parsedDate : null;
                episodes.Add(new(
                    episodeId.Value,
                    seasonNumber.Value,
                    episodeNumber.Value,
                    title,
                    airDate,
                    Still(GetString(episode, "still_path"))));
            }
        }

        return new(showId, showTitle, episodes
            .OrderBy(x => x.SeasonNumber)
            .ThenBy(x => x.EpisodeNumber)
            .ToList());
    }

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
                var retry = ClampRetryDelay(response.Headers.RetryAfter?.Delta);
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

    private static string? Still(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : $"https://image.tmdb.org/t/p/w300{path}";

    private static SearchResult? ParseSearchResult(TmdbMediaKind kind, JsonElement item)
    {
        if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id) || id <= 0)
            return null;

        var titleProperty = kind == TmdbMediaKind.Movie ? "title" : "name";
        var originalTitleProperty = kind == TmdbMediaKind.Movie ? "original_title" : "original_name";
        var dateProperty = kind == TmdbMediaKind.Movie ? "release_date" : "first_air_date";
        var title = GetString(item, titleProperty) ?? GetString(item, originalTitleProperty) ?? $"TMDB {id}";
        var originalTitle = GetString(item, originalTitleProperty) ?? title;
        var posterPath = GetString(item, "poster_path");
        var overview = GetString(item, "overview") ?? string.Empty;
        var rating = item.TryGetProperty("vote_average", out var ratingElement) && ratingElement.TryGetDouble(out var value)
            ? value
            : 0;
        DateOnly? date = DateOnly.TryParse(GetString(item, dateProperty), out var parsedDate) ? parsedDate : null;

        return new SearchResult(kind, id, title, originalTitle, date, Poster(posterPath), overview, rating, BuildUri(kind, id).ToString());
    }

    private static string? GetString(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt32(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private async Task<JsonApiResponse> RequestJsonAsync(
        string route,
        TmdbLinkFixerSettings settings,
        string operation,
        CancellationToken cancellationToken)
    {
        await WaitForRateSlotAsync(settings.RequestsPerSecond, cancellationToken).ConfigureAwait(false);
        if (!TmdbLinkFixerSettingsStore.IsBearerToken(settings.ApiCredential))
            route += $"{(route.Contains('?') ? "&" : "?")}api_key={Uri.EscapeDataString(settings.ApiCredential)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            if (TmdbLinkFixerSettingsStore.IsBearerToken(settings.ApiCredential))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiCredential);
            using var response = await clientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return JsonApiResponse.Failure("TMDB rejected the configured API credential.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = ClampRetryDelay(response.Headers.RetryAfter?.Delta);
                logger.LogWarning("TMDB returned 429; pausing requests for {Delay}", retry);
                await PauseAllRequestsAsync(retry, cancellationToken).ConfigureAwait(false);
                return JsonApiResponse.Failure($"TMDB rate-limited the request. Requests were paused for {Math.Ceiling(retry.TotalSeconds)} seconds.");
            }
            if (!response.IsSuccessStatusCode)
                return JsonApiResponse.Failure($"TMDB API request failed (HTTP {(int)response.StatusCode}).");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return JsonApiResponse.Success(document.RootElement.Clone());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return JsonApiResponse.Failure("The TMDB API request timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "TMDB API request failed while loading {Operation}", operation);
            return JsonApiResponse.Failure("The TMDB API could not return readable episode data.");
        }
    }

    private static TimeSpan ClampRetryDelay(TimeSpan? retryAfter)
    {
        var retry = retryAfter ?? TimeSpan.FromSeconds(30);
        if (retry < TimeSpan.FromSeconds(1)) retry = TimeSpan.FromSeconds(1);
        if (retry > TimeSpan.FromMinutes(5)) retry = TimeSpan.FromMinutes(5);
        return retry;
    }

    private static string KindLabel(TmdbMediaKind kind) => kind == TmdbMediaKind.Movie ? "movie" : "show";

    private enum ApiEntityState { Exists, Missing, Error }

    private sealed record ApiEntityResult(ApiEntityState State, string? PosterUrl, string? ErrorMessage, bool Fatal)
    {
        public static ApiEntityResult Exists(string? posterUrl) => new(ApiEntityState.Exists, posterUrl, null, false);
        public static ApiEntityResult Missing() => new(ApiEntityState.Missing, null, null, false);
        public static ApiEntityResult Error(string message, bool fatal = false) => new(ApiEntityState.Error, null, message, fatal);
    }

    private sealed record JsonApiResponse(JsonElement? Element, string? Error)
    {
        public static JsonApiResponse Success(JsonElement element) => new(element, null);
        public static JsonApiResponse Failure(string error) => new(null, error);
    }
}

public sealed record TmdbSearchResponse(IReadOnlyList<SearchResult> Results, string? Error)
{
    public static TmdbSearchResponse Success(IReadOnlyList<SearchResult> results) => new(results, null);
    public static TmdbSearchResponse Failure(string error) => new([], error);
}

public sealed record ProbeResult(LinkHealth Health, string? Message, TmdbMediaKind? RedirectKind, int? RedirectId, string? RedirectPosterUrl, bool Fatal)
{
    public static ProbeResult Valid() => new(LinkHealth.Valid, null, null, null, null, false);
    public static ProbeResult Invalid(string message) => new(LinkHealth.Invalid, message, null, null, null, false);
    public static ProbeResult Error(string message, bool fatal = false) => new(LinkHealth.Error, message, null, null, null, fatal);
    public static ProbeResult Redirected(TmdbMediaKind kind, int id, string? posterUrl, string message) => new(LinkHealth.Redirected, message, kind, id, posterUrl, false);
}
