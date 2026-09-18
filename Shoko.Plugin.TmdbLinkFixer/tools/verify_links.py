#!/usr/bin/env python3
"""Independent TMDB link verifier.

Reads every Shoko series' linked TMDB movies/shows from Shoko's core v3 API,
then probes each unique (kind, tmdbId) against the TMDB API and reports any
that do not resolve. Intentionally bypasses the TmdbLinkFixer plugin so its
conclusion can be cross-checked.

Credentials are read from:
  - SHOKO_URL / SHOKO_API_KEY in the repo .env (gitignored)
  - TMDB: ApiCredential in the plugin settings file, passed via --tmdb-settings
None are echoed.
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

import re


def load_env(path):
    env = {}
    try:
        for line in open(path, "r", encoding="utf-8"):
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            k, v = line.split("=", 1)
            env[k.strip()] = v.strip().strip('"').strip("'")
    except FileNotFoundError:
        pass
    return env


def shoko_series(shoko_url, apikey, page_size=100):
    """Yield all series objects from GET /Series, paginated."""
    page = 1
    while True:
        url = (
            f"{shoko_url.rstrip('/')}/api/v3/Series"
            f"?pageSize={page_size}&page={page}&includeDataFrom=TMDB"
        )
        req = urllib.request.Request(url)
        req.add_header("apikey", apikey)
        req.add_header("Accept", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                body = json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            print(f"ERROR: Shoko /Series page {page}: HTTP {e.code} {e.read().decode()[:200]}")
            sys.exit(1)
        items = body.get("List", body) if isinstance(body, dict) else body
        if not items:
            break
        for item in items:
            yield item
        if len(items) < page_size:
            break
        page += 1


def probe(kind, tmdb_id, tmdb_api_key):
    endpoint = "movie" if kind == "movie" else "tv"
    query = urllib.parse.urlencode({"language": "en-US"})
    url = f"https://api.themoviedb.org/3/{endpoint}/{tmdb_id}?{query}"
    req = urllib.request.Request(url)
    if tmdb_api_key.lower().startswith("ey"):
        req.add_header("Authorization", "Bearer " + tmdb_api_key)
    else:
        url = f"https://api.themoviedb.org/3/{endpoint}/{tmdb_id}?{query}&api_key={tmdb_api_key}"
        req = urllib.request.Request(url)
    try:
        with urllib.request.urlopen(req, timeout=20) as resp:
            return resp.getcode()
    except urllib.error.HTTPError as e:
        return e.code
    except Exception as e:
        return f"ERR:{type(e).__name__}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--env", default=".env")
    ap.add_argument("--tmdb-settings", required=True,
                    help="path to TmdbLinkFixer.settings.json (ApiCredential)")
    ap.add_argument("--rate", type=float, default=1.0)
    args = ap.parse_args()

    env = load_env(args.env)
    shoko_url = env.get("SHOKO_URL")
    apikey = env.get("SHOKO_API_KEY")
    if not shoko_url or not apikey:
        print("ERROR: SHOKO_URL and SHOKO_API_KEY must be set in .env")
        sys.exit(1)

    try:
        settings = json.load(open(args.tmdb_settings, "r", encoding="utf-8"))
    except Exception as e:
        print(f"ERROR reading tmdb settings {args.tmdb_settings}: {e}")
        sys.exit(1)
    tmdb_key = settings.get("ApiCredential")
    if not tmdb_key:
        print("ERROR: no ApiCredential in tmdb settings")
        sys.exit(1)

    # 1. Collect unique (kind, tmdbId) -> first matching series for context
    targets = {}
    series_count = 0
    for series in shoko_series(shoko_url, apikey):
        series_count += 1
        tmdb = series.get("TMDB") or {}
        for m in tmdb.get("Movies") or []:
            tid = m.get("ID")
            if tid:
                targets.setdefault(("movie", tid), (series.get("Name", ""), series.get("IDs", {}).get("Shoko")))
        for s in tmdb.get("Shows") or []:
            tid = s.get("ID")
            if tid:
                targets.setdefault(("show", tid), (series.get("Name", ""), series.get("IDs", {}).get("Shoko")))
    if series_count == 0:
        print("ERROR: no series returned from Shoko API")
        sys.exit(1)

    print(f"Scanned {series_count} series; {len(targets)} unique TMDB targets")

    # 2. Probe each unique target once, with throttling
    problems = []
    errors = []
    valid = 0
    interval = 1.0 / max(1.0, min(10.0, args.rate))
    for i, ((kind, tid), (series_name, sid)) in enumerate(targets.items(), 1):
        code = probe(kind, tid, tmdb_key)
        label = f"{kind}:{tid}"
        if code == 200:
            valid += 1
        elif code == 404:
            problems.append((kind, tid, series_name, sid, "404 (missing)"))
        else:
            errors.append((kind, tid, series_name, sid, f"HTTP {code}"))
        if i % 200 == 0:
            print(f"  ... {i}/{len(targets)} probed, {valid} valid, {len(problems)} missing, {len(errors)} errors")
        time.sleep(interval)

    # 3. Report
    print(f"\n=== RESULT ===")
    print(f"Total unique targets: {len(targets)}")
    print(f"Valid (HTTP 200): {valid}")
    print(f"Missing (404): {len(problems)}")
    print(f"Errors/other: {len(errors)}")

    if problems:
        print("\nMISSING (404) targets:")
        for kind, tid, sname, sid, why in sorted(problems):
            print(f"  {kind}:{tid}  series='{sname}' shokoSeriesID={sid}  [{why}]")
    if errors:
        print("\nERROR/other targets:")
        for kind, tid, sname, sid, why in sorted(errors):
            print(f"  {kind}:{tid}  series='{sname}' shokoSeriesID={sid}  [{why}]")

    # Only exit nonzero if we can't trust: fatal errors
    if errors and len(errors) == len(targets):
        print("\nAll probes errored — check connectivity/credential.")
        sys.exit(2)


if __name__ == "__main__":
    main()
