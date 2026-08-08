# NFO format

The plugin writes [Kodi NFO files](https://kodi.wiki/view/NFO_files) directly into the folder of each video file, plus local artwork sidecars copied from Shoko's image cache.

Files are only rewritten when the generated content actually differs from what is already on disk (the file itself is the cache). This keeps file timestamps stable and avoids pointless media-library rescans. Metadata updates always force a rewrite.

## Files

| Video type | NFO | Root element | Art sidecars |
| --- | --- | --- | --- |
| Series / TV episode | `episode.nfo` | `<episodedetails>` | `thumb.jpg` |
| Series (shared) | `tvshow.nfo` | `<tvshow>` | `poster.jpg`, `fanart.jpg` |
| Movie | `movie.nfo` | `<movie>` | `thumb.jpg` |

Art sidecars are copied only when Shoko has the artwork locally; otherwise the corresponding `<art>` entries are omitted.

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
  <uniqueid type="anidb" default="true">11294</uniqueid>
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
  <uniqueid type="anidb" default="true">11294</uniqueid>
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
- `uniqueid` includes AniDB (default) and Shoko IDs so Kodi can match the content.
- Empty/null fields are omitted entirely.
