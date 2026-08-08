# NFO format

The plugin writes [Kodi NFO files](https://kodi.wiki/view/NFO_files) and local artwork sidecars copied from Shoko's image cache. Episode NFOs are written next to their video files. TV-show NFOs and show artwork are written at the resolved show root, rather than inside a season directory.

Files are only rewritten when the generated content actually differs from what is already on disk (the file itself is the cache). This keeps file timestamps stable and avoids pointless media-library rescans. Metadata updates always force a rewrite.

## Files

| Video type | NFO | Root element | Art sidecars |
| --- | --- | --- | --- |
| Series / TV episode | `episode.nfo` | `<episodedetails>` | `thumb.jpg` |
| Series / TV show root | `tvshow.nfo` | `<tvshow>` | `poster.jpg`, `fanart.jpg` |
| Movie | `movie.nfo` | `<movie>` | `thumb.jpg` |

Art sidecars are copied only when Shoko has the artwork locally; otherwise the corresponding `<art>` entries are omitted.

For a TMDB-linked show, the show root is the deepest shared directory of local files mapped to that TMDB show, within one managed folder. This allows multiple Shoko series representing TMDB seasons to share one `tvshow.nfo`. When only one conventional `Season ...`/`S01` folder exists, its parent is used. If no safe root can be determined, the video folder is retained.

## Movie vs. TV show

TMDB data is the deciding factor. An entry is written as a movie (`movie.nfo`) when:

1. the video's episode is linked to a TMDB movie (e.g. specials to shows that TMDB treats as movies), or
2. the series is linked to a TMDB movie (e.g. OVAs that TMDB treats as movies).

If a series is linked to a TMDB show instead, it is written as a TV show regardless of AniDB's type. Only when TMDB has no links at all does the plugin fall back to AniDB's `Movie` type.

## Episode NFO

```xml
<?xml version="1.0" encoding="utf-8"?>
<episodedetails>
  <title>Re:Zero kara Hajimeru Isekai Seikatsu</title>
  <showtitle>Re:Zero Starting Life in Another World</showtitle>
  <season>1</season>
  <episode>1</episode>
  <plot>A dark fantasy isekai about Subaru Natsuki.</plot>
  <aired>2016-04-04</aired>
  <runtime>25</runtime>
  <rating>8.05</rating>
  <votes>1200</votes>
  <uniqueid type="tmdb" default="true">42509</uniqueid>
  <uniqueid type="anidb">11294</uniqueid>
  <uniqueid type="shoko">42</uniqueid>
  <thumb>thumb.jpg</thumb>
</episodedetails>
```

## TV show NFO

```xml
<?xml version="1.0" encoding="utf-8"?>
<tvshow>
  <title>Re:Zero Starting Life in Another World</title>
  <originaltitle>Re:Zero kara Hajimeru Isekai Seikatsu</originaltitle>
  <plot>A dark fantasy isekai about Subaru Natsuki.</plot>
  <premiered>2016-04-04</premiered>
  <year>2016</year>
  <rating>8.05</rating>
  <votes>1200</votes>
  <runtime>25</runtime>
  <studio>White Fox</studio>
  <genre>Drama</genre>
  <genre>Fantasy</genre>
  <uniqueid type="tmdb" default="true">64251</uniqueid>
  <uniqueid type="anidb">11294</uniqueid>
  <uniqueid type="shoko">42</uniqueid>
  <art>
    <poster>poster.jpg</poster>
    <fanart>fanart.jpg</fanart>
  </art>
</tvshow>
```

## Movie NFO

Identical to the TV show layout but with a `<movie>` root element.

## Notes

- All files are UTF-8 without a BOM.
- `uniqueid` includes TMDB when Shoko has a mapping, plus AniDB and Shoko IDs. TMDB is the default identifier when present; AniDB remains the default fallback.
- For TMDB-mapped episodes, the emitted season and episode numbers follow the mapped TMDB ordering.
- A generation pass removes old plugin-generated `tvshow.nfo` files from season folders once the same TMDB show's root NFO has been written. User-authored NFOs are not removed.
- Empty/null fields are omitted entirely.
