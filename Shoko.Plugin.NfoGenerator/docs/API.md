# NFO Generator API

All endpoints regenerate NFO files and sidecars. The response count only includes files that were actually written (`generated`).

- **Base path:** `/api/plugin/NfoGenerator`
- **Authentication:** admin (Shoko admin credentials, e.g. Basic Auth)
- **Versioning:** `ApiVersion 1` (default)

## Endpoints

### Generate a series

Regenerates NFOs for every file belonging to a series.

```
POST /api/plugin/NfoGenerator/series/{seriesID}
```

```bash
curl -X POST -u admin:password \
  http://localhost:8111/api/plugin/NfoGenerator/series/42
```

Success:

```json
{ "status": "ok", "generated": 12 }
```

`404` if the series does not exist:

```json
{ "status": "error", "message": "Series 42 not found" }
```

### Generate an episode

Regenerates the NFO for a single episode.

```
POST /api/plugin/NfoGenerator/episode/{episodeID}
```

```bash
curl -X POST -u admin:password \
  http://localhost:8111/api/plugin/NfoGenerator/episode/1337
```

`404` if the episode does not exist:

```json
{ "status": "error", "message": "Episode 1337 not found" }
```

### Generate an import folder

Regenerates NFOs for every available video file inside an import (managed) folder, then removes orphan subfolders that contain only recognized plugin output.

```
POST /api/plugin/NfoGenerator/folder/{folderID}
```

```bash
curl -X POST -u admin:password \
  http://localhost:8111/api/plugin/NfoGenerator/folder/3
```

`404` if the folder does not exist:

```json
{ "status": "error", "message": "Folder 3 not found" }
```

### Generate the library

Regenerates NFOs for the entire library (all import folders), then runs the orphan NFO/artwork and generated-only directory sweep. This can take a while on large libraries.

```
POST /api/plugin/NfoGenerator/library
```

```bash
curl -X POST -u admin:password \
  http://localhost:8111/api/plugin/NfoGenerator/library
```

```json
{ "status": "ok", "generated": 312 }
```
