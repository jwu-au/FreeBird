# Changelog

All notable changes to FreeBird are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [3.1.0] — 2026-06-11

### Added
- **Windows: auto-download official Xiph FLAC 1.5.0 binary** (~1.3 MB) from xiph.org with pinned SHA-256 verification, when no `flac` / `metaflac` is found alongside `fb.exe` or on PATH.
  - Triggered on first need (scan/watch encounters need for L3 integrity or FLAC tag write).
  - ZIP path-traversal safe (rejects entries with `..` or absolute paths).
  - Cleaned up partial downloads on failure.
  - Opt out: `--no-auto-download` or `FREEBIRD_NO_AUTO_DOWNLOAD=1`.
- **macOS / Linux: actionable error messaging** when `flac` / `metaflac` is missing (points at `brew install flac` / `apt install flac` etc.).
- **New subcommand: `fb install-flac`** for explicit installer trigger (CI / sysadmin / troubleshooting).
  - Flags: `--target <dir>` (default = `<fb dir>`), `--url <url>` (advanced, hidden).
- **New flags** (scan + watch):
  - `--flac-bin <path>` — override probe chain (use a specific flac binary).
  - `--no-auto-download` — disable Windows auto-download for this run.
  - `--flac-url <url>` — (hidden / advanced) override download URL; also via `FREEBIRD_FLAC_URL` env.
- **Hybrid integrity-degradation policy** when `flac` is unavailable:
  - `off` / `l1`: silent proceed.
  - `auto` (default): warn + degrade to L1, exit 0.
  - `l3`: exit 2 with install hint (fail-fast).
- **`tag-tool-missing` sidecar reason** when `metaflac` is unavailable for FLAC tag write (audio output unaffected).

### Changed
- `FlacToolIntegrityChecker` and `FlacTagWriter` no longer hardcode `"flac"` / `"metaflac"` strings — they resolve binary paths via `IFlacBinaryResolver` (probe chain: explicit override → `<fb dir>/` → PATH).
- `FlacProbe` is now a lazy thin wrapper over `IFlacBinaryResolver`; the eager-probe at process startup is unchanged (still occurs in `ScanRunner` / `WatchRunner` for the integrity-mode gate).
- `ScanRunner` and `WatchRunner` both now do a startup flac probe and emit the `auto` warning / `l3` fail-fast consistently. (`WatchRunner` previously did not; now matches `ScanRunner`.)

### Fixed
- (none specific to v3.1; v3.0.1 covered the metadata-fetch infinite loop.)

### Tests
- +90 new tests across resolver, installer, hybrid degradation, CLI binding, DI registration, and a SkippableFact end-to-end Windows download test (skipped on macOS / Linux). Total: 825 passed, 4 skipped, 0 failed.

### License acknowledgment
- Windows auto-download fetches FLAC binaries distributed by Xiph.Org Foundation under their own license terms. FreeBird invokes them as separate processes only.

## [3.0.1] — 2026-06-11

### Fixed
- `fb watch` no longer loops forever re-decoding files whose musicId-named output
  already exists (the v2 → v3 upgrade case) or whose NetEase API call returned
  NotFound (the intermittent-failure case). Both modes caused ~12 NetEase API
  calls per file per minute at the default poll interval. Fix: a JSON resolution
  marker at `<output>/.freebird-resolved/<source_stem>.json` records every
  (non-offline) resolution attempt with freshness keys (source size + mtime),
  the resolved filename, the naming template used, and (for failures) a
  reason-differentiated `retry_after` timestamp.
- Multi-bitrate caches (two `.uc!` files sharing one NetEase musicId) are now
  each tracked independently via per-source-stem markers; the higher-bitrate
  file wins under default `--on-collision Overwrite`.

### Removed (BREAKING for users with automation that parses `.flac.txt`)
- The `<output>.flac.txt` audit sidecar introduced in v3.0.0 (T18) is no longer
  written. All audit fields (format, integrity, reason, source path, source
  size/mtime, resolved-at, template used, source stem, music id) are now
  consolidated in the per-source-stem JSON marker under `.freebird-resolved/`.
  Existing `.flac.txt` files on disk are harmless leftovers; delete at leisure.
  If you have automation that parses them, migrate to reading the JSON markers.

### Internal
- `FilesystemSkipDecider` gains Branch 3b (marker-aware short-circuit) between
  the existing Branch 3a (stem-equals-output) and Branch 3c (bootstrap re-decode);
  the bootstrap log line is demoted from INF to DBG.
- Retry intervals (hardcoded; CLI override considered for v3.1):
  `metadata-fetch-failed` = 1h, `metadata-empty` = 7d, `metadata-deserialize-failed` = 24h.
- Bootstrap pass respects existing `--concurrency` and `--api-rate-limit` knobs;
  no special throttle. Users with large caches should expect a one-time
  multi-minute first poll after upgrade.

## [3.0.0] — 2026-06-10

### ⚠️ BREAKING

- **Output filenames now come from NetEase Cloud Music metadata by default.**
  v2 produced `<musicId>.<ext>` (e.g. `3367798042.flac`); v3 produces
  `<artist> - <title>.<ext>` (e.g. `Foo - Bar.flac`). Run with `--offline` to
  preserve v2 behaviour.
- **Failed metadata lookups now drop a `.txt` sidecar** next to the decoded file
  with `reason: <token>` (tokens: `metadata-empty`, `metadata-fetch-failed`,
  `metadata-deserialize-failed`). v2 did not produce sidecars for this case.

### Added

- `--naming-template` flag (default `"{artist} - {title}"`) on `fb scan` and
  `fb watch`. Supports `{artist}`, `{title}`, `{album}`, `{musicId}` placeholders
  with cross-platform filename sanitization.
- `--offline` switch on both subcommands — skip API, use musicId fallback.
- `--api-timeout <seconds>` (range 1–300, default 10) and
  `--api-rate-limit <req/sec>` (range 0–100, default 0 = unlimited) on both
  subcommands.
- `--write-tags` switch on both subcommands. Writes ARTIST / TITLE / ALBUM tags
  via `metaflac` (FLAC), native ID3v2.3 (MP3), or native iTunes atoms (M4A).
- `NetEaseApiClient` — HTTP client for the NetEase Cloud Music song-detail API
  (`https://music.163.com/api/song/detail/`). Per-instance URL with
  `FB_NETEASE_BASEURL` env-var override for E2E testing.
- `MetadataResolver` — maps API outcomes to one of: `Success(SongInfo)`,
  `MetadataEmpty`, `MetadataFetchFailed`, `MetadataDeserializeFailed`,
  `Offline` (5-case union).
- `MetadataAwareFileNamer` — renders the filename from the per-run template +
  resolved metadata; falls back to musicId when metadata is null.
- E2E test suite: `MetadataE2ETests` × 5 scenarios via a loopback
  `StubNetEaseServer` (HttpListener).

### Changed

- `IFileNamer.GetTargetName(...)` gains optional `string? namingTemplate = null`
  parameter. `MetadataAwareFileNamer` reads the template from the per-run param
  rather than a DI-injected `DefaultMetadataOptions`, so `--naming-template`
  actually flows end-to-end.
- `ScanOptions` and `WatchOptions` now implement `IMetadataOptions`; the
  metadata flags propagate from CLI → records → `FileProcessor` → namer.

### Migration from v2

1. **Stay offline first:** `fb scan <dir> -o <out> --offline` — verifies v3 still
   produces v2-equivalent filenames on your cache.
2. **Drop `--offline`:** new files get metadata-based names; existing files are
   skipped per `--collision`.
3. **Opt into tags:** add `--write-tags` (requires `metaflac` for FLAC). Existing
   decoded files are not re-tagged; delete and re-decode if needed.

See README "What's new in v3" for full details.

## [2.0.0] — prior

- Watch-mode subcommand (`fb watch`) for polling a NetEase cache directory.
- Filesystem-as-state sidecars for permanently-failed files.

## [1.0.0] — initial

- One-sweep decoding of `.uc` / `.uc!` files via XOR `0xA3`.
- Magic-byte format sniff (MP3 / FLAC / M4A).
- 4-level audio integrity verification (auto / l1 / l3 / off).
- Atomic write via staging + rename.
- Quarantine on failure with `.txt` sidecar.
