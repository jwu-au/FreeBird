# Changelog

All notable changes to FreeBird are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
