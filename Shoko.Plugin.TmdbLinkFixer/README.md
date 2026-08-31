# Shoko.Plugin.TmdbLinkFixer v0.1.0

Checks existing AniDB-to-TMDB movie and show links and provides an administrator-controlled correction workflow in Shoko.

## Features

- Scans every TMDB movie and show link without modifying it.
- Detects valid targets, missing targets, and TMDB redirects to another media type or ID.
- Searches TMDB movies and shows from the review page.
- Shows AniDB, current TMDB, and proposed TMDB links together.
- Shows the relevant poster when a link is hovered and a three-poster comparison before confirmation.
- Supports candidates found through a TMDB redirect or a manual movie/show search.
- Never accepts a candidate automatically. A replacement requires a final checkbox confirming the exact source and target IDs, followed by an explicit button click.

## Usage

After installation and a Shoko restart, open:

`Settings -> Plugins -> TMDB Link Fixer -> TMDB Link Fixer`

1. Click **Check all links**.
2. For a TMDB redirect, click **Review found link**; otherwise click **Search manually**.
3. Inspect the AniDB, current TMDB, and proposed TMDB pages and posters.
4. Select the AniDB episode when the proposed target is a movie.
5. Confirm the exact IDs with the checkbox and click **Accept this exact replacement**.

Only Shoko administrators can read link data, search, or accept a replacement. The embedded page itself is public so Shoko can display it, but every data and write endpoint performs an admin check.

## How validation works

The scanner requests the public `themoviedb.org/movie/{id}` or `themoviedb.org/tv/{id}` page. It follows redirects itself and records whether TMDB kept the same media type and ID, redirected to another target, returned 404/410, or could not be reached. No separate TMDB API key is needed.

Replacement searches use Shoko's existing TMDB search service. Search results and redirect suggestions are inert until an administrator completes the final confirmation.

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
- The scanner validates the linked movie/show entity. It does not individually validate every generated TMDB episode cross-reference.
- If Shoko fails after adding the new link but before removing the old one, both links may remain. Refresh the page and inspect the Shoko log; the plugin reports this partial-failure case rather than guessing what to delete.
