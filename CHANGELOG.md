# Changelog

All notable changes to FreeBird are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [3.5.0] — 2026-06-19

### Added
- **Windows Service mode**: new `fb service` subcommand tree — `init`, `install`, `uninstall`, `start`, `stop`, `restart`, `status` (7 visible) plus a hidden `run` SCM entrypoint. Registers FreeBird as a native Windows Service that wraps the `fb watch` pipeline.
- **JSON service config**: `fb service init` generates a default config; schema shipped at `schemas/service.config.json`; loaded/validated by `JsonConfigLoader`.
- **Windows Event Log integration**: service writes to the `FreeBird` source in the Application log; source created on install, removed on uninstall.
- **Rolling daily file logs** in service mode at `%ProgramData%\FreeBird\logs\watch-YYYY-MM-DD.log`, with automatic fallback to the ProgramData default when a configured `log_file` is unwritable.
- **Admin elevation + service-account support**: `--service-account` / `--service-password` (or `FB_SERVICE_PASSWORD`), with a LocalSystem-vs-user-profile-path warning.
- **Attempt-aware metadata retry backoff**: failed metadata lookups now climb an exponential, per-source schedule instead of a flat wait. Transient connectivity errors retry fast (1m → 5m → 15m → 1h → 6h) so a service that starts before the network is up recovers quickly; rate-limited responses back off more gently (30s → 2m → 10m → 30m → 2h) and honour a server `Retry-After` header (clamped to 6h).
- **Stale fallback cleanup**: when a file first decoded under a fallback name (`<musicId>.<ext>`) is later re-resolved successfully, the correctly-named file is written and the old fallback artifact is removed — but only when it is provably the same untouched FreeBird output (size + mtime + source-freshness checks); otherwise it is kept and logged. A file that is locked/in-use at delete time is kept with a warning.
- **Resolution marker schema 2**: adds `attempt_count`, `output_size`, `output_mtime` (additive, nullable). Older schema-1 markers still parse unchanged.

### Fixed
- **NetEase rate-limit / risk-control no longer mistaken for “not found”**: the API client now inspects the response body `code` (and HTTP status), so a throttled / geo-blocked reply (`HTTP 200 {code:-460,"Cheating"}` / `-447`, or HTTP 429/403/5xx) is classified as *rate-limited* and retried with backoff, instead of being treated as a genuine empty result and cold-stored for 7 days. Genuine not-found (HTTP 200, success code, no songs) is still 7 days; malformed/unknown responses are 24h.
- **`fb service` config path**: the `%ProgramData%` token in the default config path is now expanded before use, so `install`, `run`, `start`, `stop`, `restart`, `status`, and `init` all resolve to the real `C:\ProgramData\FreeBird\config.json` instead of failing with `Config file not found: %ProgramData%\FreeBird\config.json`. Env-variable tokens (e.g. `%USERPROFILE%`) in a user-supplied `--config` / `--output` are also expanded.

### Documentation
- README 'Running as a Windows Service' section + macOS launchd / Linux systemd power-user snippets + platform support matrix.

### Changed
- **Windows Event Log is now Error-only**: the service’s Event Log sink was raised from `Warning` to `Error`, so the Windows Application log shows only actionable failures. Warnings remain in the rolling file log at `%ProgramData%\FreeBird\logs`.

**No breaking changes.** `fb scan`, `fb watch`, and `fb install-flac` are unchanged.

## [3.4.1] — 2026-06-13

### Fixed
- **CI ubuntu test pollution**: `MultiInputArityTests.Scan_ZeroInputDirs_ParseError` failed on ubuntu-22.04 due to xUnit running test classes in parallel while `ScanRunner.RunnerOverride` is a process-wide static. Captured state from `ScanRunnerEmptyDirTests` leaked across class boundary, polluting the zero-input assertion. Fixed by:
  - Removing fragile `captured.Should().BeNull()` assertion (real signal `exit ≠ 0` preserved)
  - Adding `[Collection("RunnerOverride")]` to all test classes that mutate `ScanRunner.RunnerOverride`, `WatchRunner.OrchestratorFactoryOverride`, or `WatchRunner.CoordinatorFactoryOverride` (~20 classes affected)

### Notes
- No production code changes
- macOS and Windows CI were already passing; only ubuntu was affected
- Future v3.5 may convert these statics to `AsyncLocal<T>` to eliminate the underlying fragility

## [3.4.0] — 2026-06-13

### Added
- **Multi-input watch & scan**: `fb scan dir1 dir2` and `fb watch dir1 dir2` accept one or more input directories; outputs share a single flat output directory.
- **GlobalApiRateLimiter** (`--api-concurrency N`, default 4, max 16): caps in-flight NetEase API requests process-wide.
- **TokenBucketRateLimiter** wires `--api-rate-limit` to actual throttling on NetEase API calls.
- **OutputPathMutexPool**: serialises writes per output path so identical filenames from different inputs cannot race.
- **WatchSupervisor**: orchestrates one WatchTask per input directory; concurrent fan-out via `Task.WhenAll`; per-task failure isolation; graceful drain on Ctrl-C.
- **HealthProbe** (5-minute interval): demotes tasks whose input directory vanished; resurrects DEAD tasks when their directory reappears.
- **WatchTask state machine**: Initializing → Active → Dead → Resurrecting → Active, with 60-second sliding crash window (3 crashes / 60s → DEAD).
- **Log enrichment**: `[watch=<basename>]` prefix on per-task log messages.

### Fixed
- `--api-rate-limit` was a silent no-op since v3.0 (configurable but never enforced). Now correctly throttles outgoing NetEase API requests via token bucket.
- Concurrent writes to the same output path (rare same-musicId race) could collide; now serialized via mutex pool.
- `OutputPathMutexPool` dispose-vs-token-dispose race (could throw `ObjectDisposedException` if pool was disposed before all tokens).

### Changed
- `WatchOptions.InputDir` (string) renamed to `WatchOptions.InputDirs` (`IReadOnlyList<string>`) — internal API only.
- `ScanOptions.InputDirectory` (string) renamed to `ScanOptions.InputDirectories` (`IReadOnlyList<string>`) — internal API only.
- `ScanRunner.RunAsync` first parameter is now `IReadOnlyList<string>` instead of `string` — internal API only.
- CLI argument name `<input-dir>` is now `<input-dirs>` (positional, accepts 1+).

### Backward compatibility
- All single-input invocations continue to work identically: `fb scan ~/cache --output ~/music` and `fb watch ~/cache --output ~/music`.
- Sidecar contract from v3.2.0 unchanged (regression-tested).
- File output naming & integrity behavior unchanged.

### Notes
- 271 new tests added (727 → 998); 997 passing + 8 skipped (platform-gated). Zero warnings.

## [3.3.2] — 2026-06-11

### Fixed
- **Windows CI E2E test fixture**: `WindowsAutoInstallE2ETests.BuildFixtureZip` was still building ZIPs with bare `Win64/flac.exe` entry names, mismatching the v3.3.1 production schema (`flac-1.5.0-win/Win64/flac.exe`). This caused the Windows-only E2E install test to fail with `Expected exit to be 0, but found 1` on every CI run since v3.3.1. (No production code change.)

### Internal
- Missed by v3.3.1 bulk sed because the fixture was in `FreeBird.Cli.Tests` (only `FreeBird.Core.Tests` was scoped).
- Audited entire src tree (`grep -rn "Win64/flac.exe"`) — confirmed zero remaining stale fixtures.
## [3.3.1] — 2026-06-11

### Fixed
- **Windows flac auto-install: ZIP entry paths now match real upstream layout**. v3.1.0 through v3.3.0 had hardcoded ZIP entry lookup paths as `Win64/flac.exe`, but the official Xiph `flac-1.5.0-win.zip` wraps all entries in a top-level `flac-1.5.0-win/` directory. Result: every auto-install attempt on Windows failed with `flac install failed: required entry missing in archive: zip entry not found: Win64/flac.exe`. Empirically verified against the real upstream ZIP.

### Internal
- Added `InstallAsync_RealUpstreamZipLayout_AllFourEntriesFound` regression guard that simulates the exact upstream ZIP layout (wrapper dir + Win32 + Win64 + docs).
- Updated all 6 existing test fixtures to use the realistic `flac-1.5.0-win/Win64/...` path prefix (they were mirroring the bug, not catching it).

### Migration notes
- No user action needed if you previously hit `required entry missing in archive`. Upgrade to v3.3.1 and re-run `fb scan` / `fb watch`; auto-install will now succeed on first attempt.
- If you manually placed `flac.exe`/`metaflac.exe` next to `fb.exe` as a workaround, you can leave them there — the resolver still finds PATH/beside-fb first.

## [3.3.0] — 2026-06-11

### Changed
- **BREAKING (default behavior): `--write-tags` now defaults to `true`** (was `false` in v3.0.x–3.2.x). Filename naming has always used resolved metadata; embedded tags now match by default. Users who relied on the previous opt-in behavior must add `--no-write-tags` (or `--write-tags=false`) to preserve tag-preservation. (`97fb207`)
- **FLAC tag write: per-key remove (preserves unrelated tags)**: `FlacTagWriter` no longer uses `metaflac --remove-all-tags`; instead it explicitly removes only ARTIST/TITLE/ALBUM before writing the resolved values. User-curated tags like GENRE, DATE, TRACKNUMBER, REPLAYGAIN_*, COMMENT, ENCODER are now preserved across `fb scan` re-runs. (`04d924e`)
- README and `--help` text updated to reflect default behavior and document the new `--no-write-tags` opt-out flag.

### Added
- New CLI flag `--no-write-tags` on both `scan` and `watch` (opt-out alias for `--write-tags=false`).

### Internal
- Added empirical `FlacTagWriter_PreservesUnrelatedTags_*` integration test (real `metaflac` subprocess; zero mocks) to lock in the per-key remove contract. (`4ec95c2`)
- Added `Scan_NoWriteTagsFlag_DisablesTagWriting` and `Watch_NoWriteTagsFlag_DisablesTagWriting` CLI tests for the opt-out flag.
- Audited 3 Core `FileProcessorTests` for stale `// WriteTags = false by default` assumption; injected explicit `with { WriteTags = false }` opt-out to preserve original test intent.

### Migration notes (for users upgrading from v3.0.x–3.2.x)
- **If your scripts/cron jobs invoke `fb scan` or `fb watch` and you do NOT want tag writing**: add `--no-write-tags` to those invocations.
- **If you want tag writing** (the new default): no change needed. Just upgrade.
- The new default still requires `metaflac` for FLAC tag writes; if `metaflac` is missing, FLAC tag write is silently skipped (recorded in sidecar as `tag-tool-missing`) — same fail-soft behavior as before.

## [3.2.0] — 2026-06-11

### Fixed
- **Watch mode infinite retry bug**: Files that permanently fail integrity check (e.g., partial downloads failing `flac -t`) are no longer re-decoded on every watch poll cycle. The root cause was a naming asymmetry: `FileProcessor` quarantined as `{musicId}.{ext}` but `FilesystemSkipDecider` globbed for `{stem}.*.txt`. Fix harmonizes `FileProcessor` to use stem-naming, matching the sibling `UnknownFormat` path. (`f9e9ff7`)
- **Multi-bitrate musicId collision**: Same musicId with different bitrates (e.g., standard + lossless of the same song) now produce distinct quarantine artifacts instead of overwriting each other. (`f9e9ff7`)

### Changed
- **BREAKING (quarantine filename schema)**: `.freebird-failed/` files for IntegrityFailed cases are now stem-named (`{musicId}-_-_{bitrate}-_-_{md5hash}.{ext}`) instead of musicId-only (`{musicId}.{ext}`). Old sidecars from v3.0.x–3.1.x will simply be ignored (no crash, no infinite loop). Optional cleanup: `rm -rf .freebird-failed/` once after upgrading.
- Sidecar files now include a `version: 3` line for forward-compatibility detection. (`09a79bd`)

### Internal
- Added 3 regression tests for the quarantine naming behavior (unit + E2E coverage). (`2e6549b`, `eb3bd82`, `d23f6dd`)
- Added `Version` property to `SidecarRecord` (nullable int; null for legacy v2 sidecars).

### Migration notes (for users upgrading from v3.0.x or v3.1.x)
- No data migration required. v3.2.0 ignores old musicId-named sidecars; they simply won't trigger skip behavior. New IntegrityFailed quarantines write the new stem-named format.
- **Optional**: If `.freebird-failed/` contains a large number of old sidecars and you want the skip behavior immediately, run `rm -rf .freebird-failed/` and let watch regenerate.

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
