# FreeBird

[![CI](https://github.com/jwu-au/FreeBird/actions/workflows/ci.yml/badge.svg)](https://github.com/jwu-au/FreeBird/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/jwu-au/FreeBird)](https://github.com/jwu-au/FreeBird/releases/latest)

One-sweep CLI to decrypt NetEase Cloud Music `.uc` / `.uc!` cache files (XOR `0xA3`) into clean MP3 / FLAC / M4A files, with optional audio-integrity verification.

> Cross-platform: macOS, Linux, Windows. Built on .NET 10.

---

## What's new in v3.1

**Bundled FLAC binary support**:
- Windows: when `flac.exe` is missing, FreeBird auto-downloads the official Xiph FLAC 1.5.0 release (~1.3 MB) from xiph.org and verifies it via pinned SHA-256.
- macOS / Linux: if `flac` is missing, FreeBird prints a clear instruction to install via `brew install flac` (macOS) or `apt install flac` (Debian/Ubuntu) and continues with structural-only checks (L1) under `--integrity auto`.
- New explicit subcommand: `fb install-flac` — useful for CI / sysadmin / pre-warming.
- New advanced flags: `--flac-bin <path>`, `--no-auto-download` (the `--flac-url` flag is hidden/advanced; also honored via `FREEBIRD_FLAC_URL` and `FREEBIRD_NO_AUTO_DOWNLOAD` env vars).

---

## What's new in v3

v3 adds **NetEase API–driven naming** and **default-on tag-writing** (v3.3+; opt-out via `--no-write-tags`) to the decoder pipeline.

### Filename rendering from real metadata

When run online (the default), `fb` now queries the NetEase Cloud Music song-detail API
for each decoded file and renames the output using a template. Out of the box:

```
3367798042.uc   →   <artist> - <title>.flac
```

The template is configurable via `--naming-template`. Recognised placeholders:
`{artist}`, `{title}`, `{album}`, `{musicId}`. Templates may contain path separators
(e.g. `"{album}/{title}"`) and will be sanitized for cross-platform filesystem safety.

When the API call fails (offline, 5xx, timeout, deserialization error, or `--offline`),
`fb` falls back to the musicId-based name:

```
3367798042.uc   →   3367798042.flac
```

and drops a `<finalname>.txt` sidecar next to it with `reason: <token>` so you can
spot which files lost metadata. Tokens: `metadata-empty`, `metadata-fetch-failed`,
`metadata-deserialize-failed`.

### New CLI flags

All five flags are available on both `fb scan` and `fb watch`:

| Flag | Type | Default | Description |
|---|---|---|---|
| `--naming-template` | string | `"{artist} - {title}"` | Filename template using `{artist}` `{title}` `{album}` `{musicId}`. |
| `--offline` | switch | `false` | Skip NetEase API; use musicId fallback naming. |
| `--api-timeout` | int (seconds) | `10` | NetEase API request timeout (range 1–300). |
| `--api-rate-limit` | int (req/sec) | `0` | Max NetEase API calls per second (0–100, 0 = unlimited). |
| `--write-tags` | switch | **`true`** | Write metadata tags (ARTIST/TITLE/ALBUM) into decoded audio files. (v3.3+) |
| `--no-write-tags` | switch | `false` | Disable tag writing (opt-out from default `--write-tags=true`). (v3.3+) |

For exact descriptions and defaults, see `fb scan --help` / `fb watch --help`.

### Tag-writing (on by default since v3.3)

FreeBird embeds the resolved metadata into the decoded audio. Tag writing is
**on by default** (`--write-tags`, since v3.3); pass `--no-write-tags` to disable.

- **FLAC** — written via `metaflac` (install with `brew install flac` on macOS, `apt install flac` on Debian/Ubuntu). Existing tags like GENRE, DATE, TRACKNUMBER, REPLAYGAIN_*, COMMENT, ENCODER are **preserved** (only ARTIST/TITLE/ALBUM are replaced).
- **MP3** — ID3v2.3 tags written natively (no external tool required).
- **M4A** — iTunes-style atoms written natively.

Tags written: ARTIST, TITLE, ALBUM. If metadata resolution falls back to musicId,
no tags are written for that file (the sidecar still records the reason).

If `metaflac` is missing (FLAC tag write only), tag write is silently skipped for
FLAC files (recorded in sidecar as `tag-tool-missing`); audio output is unaffected.

### Migration from v2 (3 steps)

v3's online-by-default behaviour is a **BREAKING change** to filenames. Recommended
migration path:

1. **First v3 run — stay offline.** Use `fb scan ... --offline` to confirm v3 still
   produces the v2-equivalent filenames (musicId-based) on your existing cache.
2. **Drop `--offline`.** Re-run without the flag; new files are renamed from
   metadata. Existing decoded files are skipped per `--collision` policy.
3. **Tags now on by default (v3.3+).** Tag writing was opt-in in v3.0–v3.2, now
   on by default. If you want to preserve v3.2-era behavior of not writing tags,
   pass `--no-write-tags`. Re-decodes are NOT triggered automatically; you may
   want to delete prior outputs and re-run if you want tags on previously-decoded files.

### v3 edge cases worth knowing

- **OA2 — re-decode after offline-only runs.** If you previously ran with
  `--offline` and then run again WITHOUT `--offline`, the conservative collision
  policy treats the existing musicId-named file as already-decoded; the new
  metadata-named file will NOT be produced unless you use `--collision overwrite`
  or delete the prior output. This is intentional — see CHANGELOG for rationale.
- **OA1 — collisions.** When two different musicIds resolve to the same
  `"<artist> - <title>"`, the second is suffixed with the musicId for
  disambiguation (e.g. `"Foo - Bar [3367798042].flac"`).

---

## Features

- **Scans a directory** for `.uc` (Windows) and `.uc!` (macOS) NetEase cache files.
- **Decrypts** with the XOR-`0xA3` algorithm used by the NetEase Cloud Music client.
- **Sniffs format** from magic bytes (MP3 ID3 / MP3 sync / FLAC `fLaC` / M4A `ftyp`).
- **Verifies integrity** at one of four levels (`auto` / `l1` / `l3` / `off`).
- **Atomic writes** via staging directory + rename — never leaves half-written files.
- **Quarantines failures** to `.freebird-failed/` with a `.txt` sidecar capturing the reason.
- **Skip-or-overwrite collision policy** for repeat runs.
- **Concurrent** processing with configurable worker count.
- **Metadata-aware naming** — renames output files from NetEase Cloud Music song-detail API; falls back to musicId on failure. ARTIST/TITLE/ALBUM tags are embedded by default (v3.3+; opt-out via `--no-write-tags`); existing unrelated tags (GENRE/DATE/etc) are preserved.

---

## Install

### Build from source

```
git clone <this repo>
cd FreeBird
dotnet build -c Release
```

The `fb` (Windows: `fb.exe`) binary will be at `src/FreeBird.Cli/bin/Release/net10.0/`.

### `flac` binary dependency (v3.1)

FreeBird needs the official `flac` binary for L3 (PCM-MD5) integrity verification on FLAC files, and `metaflac` for FLAC tag writing.

| OS | How FreeBird finds `flac` |
|---|---|
| **Windows** | **Auto-download** on first need: official Xiph 1.5.0 release is fetched from xiph.org, SHA-verified, and extracted alongside `fb.exe`. No manual install. To opt out: `--no-auto-download` or set `FREEBIRD_NO_AUTO_DOWNLOAD=1`. To pre-warm in CI: `fb install-flac`. |
| **macOS** | `brew install flac` |
| **Linux (Debian/Ubuntu)** | `sudo apt install flac` |
| **Linux (Fedora)** | `sudo dnf install flac` |
| **Linux (Arch)** | `sudo pacman -S flac` |
| **Docker** | `RUN apt install -y flac && rm -rf /var/lib/apt/lists/*` |

Probe order (all platforms): `--flac-bin <path>` → `<fb dir>/flac{.exe}` → `flac` on PATH.

Without `flac`:
- `--integrity off` and `--integrity l1` work fine (no FLAC L3 features).
- `--integrity auto` (default) gracefully degrades to L1 with a one-time warning.
- `--integrity l3` exits 2 with an install hint.

Without `metaflac` (FLAC tag writing): tag write is silently skipped for FLAC files (recorded in sidecar as `tag-tool-missing`), audio output is unaffected.

---

## Usage

```
fb scan <input-dir> --output <output-dir> [options]
```

### Example

```bash
# macOS — decode cached songs into ~/Music/decoded
fb scan "~/Library/Containers/com.netease.163music/Data/Caches/online_play_cache" \
        --output ~/Music/decoded
```

```powershell
# Windows — decode cached songs into D:\Music\decoded
fb.exe scan "C:\Users\you\AppData\Local\Netease\CloudMusic\Cache\Cache" `
            --output D:\Music\decoded
```

### Options

| Flag | Default | Description |
|------|---------|-------------|
| `-o, --output <dir>` | required | Output directory for decoded files. Created if missing. |
| `--integrity <level>` | `auto` | One of `auto` / `l1` / `l3` / `off`. See [Integrity levels](#integrity-levels). |
| `--concurrency <n>` | `4` | Number of files processed in parallel. |
| `--on-collision <policy>` | `skip` | `skip` or `overwrite` when the output file already exists. |
| `-v, --verbose` | `false` | Debug-level logging. |
| `-q, --quiet` | `false` | Warning-level only. Mutually exclusive with `--verbose`. |

Get inline help:

```bash
fb --help
fb scan --help
fb watch --help
```

### Advanced flags (all subcommands, v3.1+)

| Flag | Env var | Default | Purpose |
|---|---|---|---|
| `--flac-bin <path>` | — | (auto-probe) | Force a specific `flac` binary location, skipping the probe chain. |
| `--no-auto-download` | `FREEBIRD_NO_AUTO_DOWNLOAD=1` | off | Disable the Windows auto-download. Equivalent to having no `flac` on PATH. |
| `--flac-url <url>` | `FREEBIRD_FLAC_URL` | pinned Xiph 1.5.0 | (hidden) Override download source. For air-gapped networks or alternative mirrors. Used by `fb install-flac` and the auto-install path on Windows. |

### `fb install-flac` (Windows only — pre-warm the flac binary)

Explicitly trigger the Windows auto-download outside of a scan/watch cycle. Useful for:
- CI: install flac before the first scan to avoid first-run race.
- Sysadmin: install to a custom directory.
- Troubleshooting: re-install after corruption.

```
fb install-flac
fb install-flac --target C:\tools\flac
fb install-flac --target /opt/flac --url https://your-mirror.example/flac.zip
```

On macOS / Linux: prints a polite message pointing at the OS package manager (brew/apt) and exits 0.

---

## `fb watch`

Watch an input directory for new/changed `.uc` files and decode them as they stabilize. Runs until Ctrl-C.

```
fb watch <input-dir> --output <output-dir> [options]
```

### Example

```bash
fb watch ~/Library/Caches/com.netease.163music/Caches -o ~/Music/Decoded
```

### Options

| Flag | Default | Description |
|------|---------|-------------|
| `-o, --output <dir>` | required | Output directory for decoded files. Created if missing. |
| `--integrity <level>` | `auto` | One of `auto` / `l1` / `l3` / `off`. See [Integrity levels](#integrity-levels). |
| `--concurrency <n>` | `4` | Max files processed in parallel (1-32). |
| `--on-collision <policy>` | `skip` | `skip` or `overwrite` when the output file already exists. |
| `--poll-interval <Ns\|Nm>` | `5s` | How often to poll the input directory. Range `1s`..`60m`. |
| `--stability-checks <n>` | `2` | Consecutive equal-size polls required before treating a file as complete (1-10). |
| `--min-file-size <bytes>` | `1024` | Skip files smaller than N bytes. |
| `--skip-initial-scan` | `false` | Skip the initial pass over existing files; only process files that appear after startup. |
| `--log-file <path>` | `<output>/.freebird/logs/watch-YYYY-MM-DD.log` | Path to the rolling watch log file. |
| `--no-log-file` | `false` | Disable the rolling watch log file. Mutually exclusive with `--log-file`. |
| `-v, --verbose` | `false` | Debug-level logging. |
| `-q, --quiet` | `false` | Warning-level only. Mutually exclusive with `--verbose`. |

### Re-decoding files

There is no separate "retry" command. To re-process a file, delete the corresponding output:

```bash
# Re-decode a successfully processed file
rm output/foo.flac

# Retry a quarantined failure
rm output/.freebird-failed/foo.flac.txt
```

The next poll cycle will see the missing output and decode the source again.

### Log file

By default `fb watch` writes a rolling log to:

```
<output>/.freebird/logs/watch-YYYY-MM-DD.log
```

- Rolls daily.
- 14-day retention (older files are deleted automatically).
- Override the path with `--log-file <path>`.
- Disable file logging entirely with `--no-log-file` (console output is unaffected).

### Exit codes

Same mapping as `fb scan` — see [Exit codes](#exit-codes). Ctrl-C / SIGTERM exits with code `130`.

---

## Integrity levels

| Level | What it does | When to use |
|-------|-------------|-------------|
| `off` | No verification. Fastest. | You trust your inputs and want raw decryption speed. |
| `l1`  | Structural check via TagLib# (header parse + duration > 0). Works for MP3, FLAC, M4A. | Default light check. |
| `l3`  | Full FLAC PCM-MD5 verification via external `flac -t`. **FLAC only.** Falls back to `l1` for MP3 / M4A. | Maximum confidence for FLAC. Requires `flac` binary. |
| `auto` *(default)* | Probes `flac` at startup. Uses `l3` for FLAC if available, else `l1`. Always uses `l1` for MP3 / M4A. | Recommended for most users. |

If you use `--integrity l3` without `flac` on `PATH`, FreeBird exits with code `2` and a clear error message — it will not silently downgrade.

---

## Output layout

```
<output-dir>/
├── 12345-abc.mp3            ← successfully decoded files
├── 67890-def.flac
├── .freebird-staging/       ← temporary; auto-cleaned on success
└── .freebird-failed/        ← quarantined files
    ├── bad-song.flac
    └── bad-song.flac.txt    ← sidecar with failure reason
```

### Sidecar format

```
timestamp: 2026-06-08T22:14:02.0870900Z
source:    /path/to/input/bad-song.uc
format:    Flac
integrity: L3
reason:    flac -t failed: ... FRAME_CRC_MISMATCH after processing 53000 samples
```

---

## Exit codes

| Code | Meaning |
|------|---------|
| `0`  | All files decoded successfully (or input was empty). |
| `1`  | One or more files failed integrity check, had unknown format, or threw an error. |
| `2`  | Bad arguments (missing input dir, `--integrity l3` without `flac`, `--verbose` + `--quiet`, etc.). |
| `130`| Cancelled via Ctrl-C. |

---

## How it works

For each `.uc` / `.uc!` file in the input directory:

1. **Decrypt** every byte with `XOR 0xA3` (streamed, low memory).
2. **Sniff** the first 12 bytes to identify MP3 / FLAC / M4A. Unknown → quarantine.
3. **Atomically write** to `.freebird-staging/<guid>.<ext>`.
4. **Verify integrity** at the selected level.
5. If passed → atomic rename to `<output>/<stem>.<ext>`.
6. If failed → move to `.freebird-failed/` with a `.txt` sidecar.

No files in the input directory are ever modified or deleted.

---

## Limitations

- **`flac` binary must be on `PATH`** for L3 — no config-file override yet.
- **No recursive directory scan** — input directory is scanned non-recursively.
- Windows auto-download requires write access to the `fb.exe` directory (typically true for user installs; system-wide installs to `Program Files` require admin or `--no-auto-download`).
- Auto-download URL and SHA are pinned to Xiph FLAC 1.5.0. Newer FLAC versions on PATH still work; the auto-download is just for the bundled-fallback case.
- `fb install-flac` on macOS/Linux only prints package-manager hints (no auto-install) to respect upstream maintainer-managed installs.
- **Upgrading from v3.0.x or v3.1.x**: If `.freebird-failed/` contains old sidecars from previous versions, they will be silently ignored (no crash). To regenerate them with the new stem-based naming, optionally run `rm -rf .freebird-failed/` once after upgrading.

---

## Development

```bash
dotnet test                # 378 tests, all passing
dotnet build               # 0 warnings, 0 errors
dotnet run --project src/FreeBird.Cli -- scan ./test-in --output ./test-out
```

### Project layout

| Project | Purpose |
|---------|---------|
| `src/FreeBird.Core` | XOR decoder, format sniffer, integrity checkers, file processor, orchestrator |
| `src/FreeBird.Cli` | `Program.cs` entry, System.CommandLine wiring, Autofac container, Serilog logger |
| `src/FreeBird.Core.Tests` | Unit + small-integration tests for Core |
| `src/FreeBird.Cli.Tests` | End-to-end tests through `ScanRunner` with real fixtures |

DI is wired via [Autofac](https://autofac.org/) using an `IDependency` marker-interface convention. Logging is [Serilog](https://serilog.net/) to the console.

---

## License

TBD.

### FLAC licensing acknowledgment

When used on Windows, FreeBird auto-downloads the Xiph FLAC binary (`flac.exe`, `metaflac.exe`, `libFLAC.dll`, `libFLAC++.dll`) from xiph.org. These binaries are distributed by Xiph.Org Foundation under their own license terms (the FLAC source license is BSD-style for libFLAC and GPL v2 for the `flac` / `metaflac` command-line tools). FreeBird only invokes them as separate processes (no static linking) and ships no FLAC source code or binaries in this repository. Users redistributing FreeBird should be aware of Xiph's licensing terms if they bundle the downloaded binaries: https://xiph.org/flac/license.html
