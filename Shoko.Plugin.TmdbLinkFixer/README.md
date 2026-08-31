# Shoko.Plugin.TmdbLinkFixer v0.1.4

Checks existing AniDB-to-TMDB movie and show links and provides an administrator-controlled correction workflow in Shoko.

## Features

- Scans every TMDB movie and show link without modifying it.
- Validates links through the TMDB API instead of bulk-requesting public website pages.
- Detects valid targets, missing targets, and an existing target in the alternate movie/TV namespace.
- Provides user-triggered Shoko match suggestions and manual TMDB movie/show search from the review page.
- Shows AniDB, current TMDB, and proposed TMDB links together.
- Shows the relevant poster when a link is hovered and a three-poster comparison before confirmation.
- Supports candidates found through an alternate-type check, a user-triggered suggestion search, or a manual movie/show search.
- Never accepts a candidate automatically. A replacement requires a final checkbox confirming the exact source and target IDs, followed by an explicit button click.

## Usage

After installation and a Shoko restart, open:

`Settings -> Plugins -> TMDB Link Fixer -> TMDB Link Fixer`

1. Enter a TMDB v3 API key or v4 read access token, select a rate from 1 to 10 requests/second, and save the API settings.
2. Click **Check all links**.
3. Review an alternate-type result, request unverified match suggestions for one problematic link, or search manually.
4. Inspect the AniDB, current TMDB, and proposed TMDB pages and posters.
5. Select the AniDB episode when the proposed target is a movie.
6. Confirm the exact IDs with the checkbox and click **Accept this exact replacement**.

Only Shoko administrators can read link data, search, or accept a replacement. The embedded page itself is public so Shoko can display it, but every data and write endpoint performs an admin check.

## How validation works

The scanner never bulk-requests public `themoviedb.org` pages. It calls the TMDB API endpoint for the link's recorded media type:

- `GET /3/movie/{id}` for a movie link
- `GET /3/tv/{id}` for a show link

HTTP 200 means that the entity exists. HTTP 404 means that the entity is missing from that namespace. Only after a 404 does the scanner check the same numeric ID in the other namespace. Movie and TV IDs are separate namespaces, so an existing alternate ID is only an unverified candidate and may be a completely unrelated title.

Current links are deduplicated by media type and TMDB ID before remote validation. Every unique `(movie|show, TMDB ID)` is requested once per scan even if several Shoko cross-references use it. Requests are evenly spaced and capped at 10 requests/second; the setting can be reduced as far as 1 request/second. At the default rate, 6,500 unique endpoint checks take at least about 10 minutes 50 seconds. Missing entries can take longer because the alternate media type is checked as a second request.

The scanner honors TMDB's `Retry-After` response after HTTP 429 and pauses for up to five minutes. Authentication failures stop the scan immediately instead of repeating a rejected credential across the library. See TMDB's [rate-limiting documentation](https://developer.themoviedb.org/docs/rate-limiting).

The API credential is stored in `TmdbLinkFixer.settings.json` under Shoko's configuration area. It is never returned to the browser after saving, and the plugin restricts the file to the Shoko process user on Unix-like systems where supported. Clearing the credential disables scanning. Both a TMDB v3 API key and a v4 read access token are supported.

Replacement searches use Shoko's existing TMDB search service. Automatic match suggestions are requested only for one link after the administrator clicks **Find automatic suggestions**; they are not generated in bulk during a scan. Search results, automatic suggestions, and alternate-type candidates are inert until an administrator completes the final comparison and confirmation.

When a replacement is explicitly accepted, the plugin validates the exact target again, loads its metadata, adds it as a user-verified link, and only then removes the exact old link. TMDB show links use Shoko's normal episode matching behavior after the administrator has selected and accepted that show. The plugin never chooses or accepts a result by title similarity on its own.

## Build

From the repository root with the .NET 10 SDK:

```bash
./Shoko.Plugin.TmdbLinkFixer/build.sh
```

PowerShell:

```powershell
./Shoko.Plugin.TmdbLinkFixer/build.ps1
```

Copy the contents of `Shoko.Plugin.TmdbLinkFixer/dist/` into the Shoko plugin directory and restart Shoko.

## Limitations

- A temporary TMDB network, rate-limit, or server error is reported as **Check failed**, never as an invalid link.
- The 10 requests/second setting applies to validation performed by this plugin. Shoko's search service may have its own request handling.
- The scanner validates the linked movie/show entity. It does not individually validate every generated TMDB episode cross-reference.
- If Shoko fails after adding the new link but before removing the old one, both links may remain. Refresh the page and inspect the Shoko log; the plugin reports this partial-failure case rather than guessing what to delete.
