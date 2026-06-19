# FreeBird

[![CI](https://github.com/jwu-au/FreeBird/actions/workflows/ci.yml/badge.svg)](https://github.com/jwu-au/FreeBird/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/jwu-au/FreeBird)](https://github.com/jwu-au/FreeBird/releases)
[![License](https://img.shields.io/badge/license-TBD-lightgrey.svg)](#license)

A friendly command-line tool that turns your **NetEase Cloud Music** offline cache files into clean, properly named, fully tagged **MP3 / FLAC / M4A** files you can play anywhere.

> Cross-platform: **macOS, Linux, Windows**. Built on .NET 10.

---

## Why FreeBird

When NetEase Cloud Music caches a song on your device, it stores it as an opaque `.uc` / `.uc!` file that nothing else can play. FreeBird:

- 🔓 **Decrypts** the cache file into a normal audio file
- 🎵 **Names it properly** — `Artist - Title.flac` instead of `1303027499-_-_5999-_-_...uc!`
- 🏷️  **Embeds metadata tags** (ARTIST, TITLE, ALBUM) fetched from NetEase
- 🛡️ **Verifies the audio** is not corrupted (FLAC PCM-MD5 / structural check)
- 📂 **Never touches your originals** — read-only on the cache directory
- 🎯 **Just works** — single command, no GUI, no config file

Run it once with `fb scan` to convert what you have today, or leave `fb watch` running to convert new songs as you listen.

---

## Quick start

After [installing](#install):

```bash
# Convert every cached song into ~/Music/decoded — runs once and exits
fb scan ~/Library/Containers/com.netease.163music/Data/Caches/online_play_cache \
        --output ~/Music/decoded

# Or: keep watching the cache directory and auto-convert new songs
fb watch ~/Library/Containers/com.netease.163music/Data/Caches/online_play_cache \
         --output ~/Music/decoded
```

That's it. Open `~/Music/decoded` in your favourite player and enjoy.

---

## Install

### Pre-built binaries (recommended)

Download the latest binary for your OS from the [releases page](https://github.com/jwu-au/FreeBird/releases):

- `freebird-X.Y.Z-osx-arm64.tar.gz` — macOS (Apple Silicon)
- `freebird-X.Y.Z-linux-x64.tar.gz` — Linux
- `freebird-X.Y.Z-win-x64.zip` — Windows

Extract, optionally put `fb` somewhere on your `PATH`, done.

### Build from source

```bash
git clone https://github.com/jwu-au/FreeBird.git
cd FreeBird
dotnet build -c Release
```

The `fb` (or `fb.exe`) binary lands in `src/FreeBird.Cli/bin/Release/net10.0/`.

### Optional: `flac` binary

For the strongest integrity check on FLAC files (full PCM-MD5 verification) and FLAC tag-writing, FreeBird needs the official `flac` binary.

| OS | Install command |
|---|---|
| **macOS** | `brew install flac` |
| **Linux (Debian/Ubuntu)** | `sudo apt install flac` |
| **Linux (Fedora)** | `sudo dnf install flac` |
| **Linux (Arch)** | `sudo pacman -S flac` |
| **Windows** | **Automatic** — FreeBird downloads the official Xiph release on first need, verifies its SHA, and places it next to `fb.exe`. No manual step. |

If you skip this step on macOS/Linux, FreeBird still works — it gracefully degrades to a lighter structural integrity check and silently skips FLAC tag-writing. MP3 / M4A are unaffected.

---

## Usage

### `fb scan` — one-time conversion

```
fb scan <input-dirs>... --output <output-dir> [options]
```

#### Examples

```bash
# macOS
fb scan "~/Library/Containers/com.netease.163music/Data/Caches/online_play_cache" \
        --output ~/Music/decoded
```

```powershell
# Windows
fb.exe scan "C:\Users\you\AppData\Local\Netease\CloudMusic\Cache\Cache" `
            --output D:\Music\decoded
```

#### Options

| Flag | Default | Description |
|------|---------|-------------|
| `-o, --output <dir>` | required | Output directory for decoded files. Created if missing. |
| `--integrity <level>` | `auto` | One of `auto` / `l1` / `l3` / `off`. See [Integrity levels](#integrity-levels). |
| `--concurrency <n>` | `4` | Max files processed in parallel across all inputs. |
| `--api-concurrency <n>` | `4` | Max in-flight NetEase API requests (1–16). Global across all inputs. |
| `--api-rate-limit <n>` | `0` | Max NetEase API calls/sec (0 = unlimited). |
| `--on-collision <policy>` | `skip` | `skip` or `overwrite` when an output file already exists. |
| `--no-write-tags` | `false` | Disable embedding ARTIST/TITLE/ALBUM tags. |
| `-v, --verbose` | `false` | Debug-level logging. |
| `-q, --quiet` | `false` | Warning-level only. Mutually exclusive with `--verbose`. |

### `fb watch` — continuous monitoring

Watch one or more input directories and decode new/changed files as they stabilise. Runs until you press Ctrl-C.

```
fb watch <input-dirs>... --output <output-dir> [options]
```

#### Example

```bash
fb watch ~/Library/Containers/com.netease.163music/Data/Caches/online_play_cache \
         --output ~/Music/decoded
```

#### Options

All `fb scan` options plus:

| Flag | Default | Description |
|------|---------|-------------|
| `--poll-interval <Ns\|Nm>` | `5s` | How often to poll the input directory. Range `1s`..`60m`. |
| `--stability-checks <n>` | `2` | Consecutive equal-size polls required before treating a file as complete (1-10). |
| `--min-file-size <bytes>` | `1024` | Skip files smaller than N bytes. |
| `--skip-initial-scan` | `false` | Skip the initial pass over existing files; only process files that appear after startup. |
| `--log-file <path>` | `<output>/.freebird/logs/watch-YYYY-MM-DD.log` | Path to the rolling watch log file. |
| `--no-log-file` | `false` | Disable the rolling watch log file. Mutually exclusive with `--log-file`. |

#### Re-decoding files

There is no separate "retry" command. Just delete the output:

```bash
# Re-decode a successfully processed file
rm ~/Music/decoded/"Artist - Title.flac"

# Retry a quarantined failure
rm ~/Music/decoded/.freebird-failed/foo.flac.txt
```

The next poll cycle will see the missing output and decode the source again.

#### Log file

By default `fb watch` writes a rolling log to:

```
<output>/.freebird/logs/watch-YYYY-MM-DD.log
```

- Rolls daily, 14-day retention (older files deleted automatically).
- Override the path with `--log-file <path>`.
- Disable file logging entirely with `--no-log-file` (console output is unaffected).

### Advanced flags

| Flag | Env var | Default | Purpose |
|---|---|---|---|
| `--flac-bin <path>` | — | (auto-probe) | Force a specific `flac` binary location. |
| `--no-auto-download` | `FREEBIRD_NO_AUTO_DOWNLOAD=1` | off | Disable Windows auto-download of the `flac` binary. |
| `--flac-url <url>` | `FREEBIRD_FLAC_URL` | pinned Xiph 1.5.0 | Override download source. For air-gapped networks or alternative mirrors. |

### `fb install-flac` (Windows only)

Pre-warm the `flac` auto-download outside of a scan/watch cycle — handy for CI or scripted installs.

```
fb install-flac
fb install-flac --target C:\tools\flac
fb install-flac --target /opt/flac --url https://your-mirror.example/flac.zip
```

On macOS/Linux it prints a friendly hint to use your package manager and exits.

---

## Running as a Windows Service

> Placed here (right after `fb install-flac`, before *Multiple input directories*) so all Windows-specific commands live together.

FreeBird can run as a native **Windows Service** that continuously decodes your NetEase cache in the background — it wraps the same pipeline as `fb watch`, just supervised by the OS so it survives reboots and logouts. **Windows-only**; macOS/Linux users should see the [appendix below](#macos--linux-power-users).

### Quickstart

Run these in an **elevated (Administrator) PowerShell** for the `install`/`start` steps:

```powershell
# 1. Write a default config to %ProgramData%\FreeBird\config.json
#    (use --output to choose a different path; --force to overwrite an existing one)
fb service init

# 2. Edit the config: set inputs[] to your NetEase cache dir(s) and the output dir
notepad C:\ProgramData\FreeBird\config.json

# 3. Register the service (requires elevated/Admin PowerShell)
fb service install --config C:\ProgramData\FreeBird\config.json

# 4. Start it
fb service start

# 5. Check it's running
fb service status
```

### Subcommand reference

| Command | What it does | Requires Admin |
|---|---|---|
| `fb service init` | Write a default JSON config to `%ProgramData%\FreeBird\config.json` (`--output`, `--force`). | No |
| `fb service install` | Register FreeBird as a Windows Service (`--config`, `--service-account`, `--service-password`). | Yes |
| `fb service uninstall` | Remove the registered service. | Yes |
| `fb service start` | Start the service. | Yes |
| `fb service stop` | Stop the service. | Yes |
| `fb service restart` | Restart the service (apply config changes). | Yes |
| `fb service status` | Show current state + uptime. | No |

> There is also an internal `fb service run` subcommand — it is invoked by the Windows **Service Control Manager** as the service entrypoint and is **not for interactive use**.

### Exit codes

Each subcommand returns documented exit codes — `0` for success, non-zero per failure class:

| Subcommand | Exit codes |
|---|---|
| `install` | `0` success · `1` not admin · `2` already installed · `3` config invalid · `4` SCM error |
| `start` | `0` success · `1` general error · `2` not installed · `3` already running · `4` SCM error |
| `stop` | `0` success · `1` general error · `2` not installed · `3` already stopped · `4` SCM error |
| `restart` | `0` success · `1` general error · `2` not installed · `3` SCM error |
| `status` | `0` running · `1` not installed · `2` stopped · `3` other |

### Configuration

The config file is JSON, validated against the schema shipped at `schemas/service.config.json`. Set `inputs[]` to one or more NetEase cache directories and pick an `output` directory. After editing the config, run `fb service restart` for changes to take effect.

### Troubleshooting

- **LocalSystem can't read user-profile paths.** The default service account (`LocalSystem`) cannot read user-profile locations like `%LocalAppData%\NetEase\...`. If your cache lives under a user profile, install with `--service-account <DOMAIN\user>` so the service runs as an identity that can read those files — a **gMSA** (group Managed Service Account) is recommended.
- **Service-account password.** Pass `--service-password` or set the `FB_SERVICE_PASSWORD` environment variable at install time. Rotate per your org policy and re-run `fb service install` afterwards.
- **Logs.** The service writes a rolling daily file at `%ProgramData%\FreeBird\logs\watch-YYYY-MM-DD.log` containing the full detail (`Information` and above). The Windows **Event Log** (source `FreeBird`, under the Application log) receives only **`Error` and above** — actionable failures — so routine warnings don't clutter Event Viewer. To diagnose a warning, check the file log.
- **A file is named `<number>.mp3` instead of `Artist - Title.mp3`.** Metadata lookup was temporarily unavailable (no network at startup, or NetEase rate-limited / risk-controlled the request — often from an overseas/cloud IP). FreeBird decodes immediately under a fallback name and **retries metadata automatically** with backoff: transient network errors retry quickly (minutes), rate-limiting backs off more gently (and honours the server's `Retry-After`). Once metadata succeeds the file is renamed correctly and the old fallback file is cleaned up. Genuine "song not in NetEase" results are retried far less often (weekly).

### macOS & Linux (power users)

FreeBird's native service mode is Windows-only, but `fb watch` is a long-running foreground process that any OS init system can supervise.

**macOS — launchd** (`~/Library/LaunchAgents/com.freebird.watch.plist`):

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.freebird.watch</string>
    <key>ProgramArguments</key>
    <array>
        <string>/usr/local/bin/fb</string>
        <string>watch</string>
        <string>/Users/me/NetEaseCache</string>
        <string>-o</string>
        <string>/Users/me/Music/FreeBird</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
</dict>
</plist>
```

Load it with `launchctl load ~/Library/LaunchAgents/com.freebird.watch.plist`.

**Linux — systemd** (`/etc/systemd/system/freebird.service`):

```ini
[Unit]
Description=FreeBird NetEase cache decoder (fb watch)
After=network.target

[Service]
ExecStart=/usr/local/bin/fb watch /home/me/NetEaseCache -o /home/me/Music/FreeBird
Restart=always

[Install]
WantedBy=multi-user.target
```

Enable it with `sudo systemctl enable --now freebird`.

### Platform support matrix

| Feature | Windows | macOS | Linux |
|---|---|---|---|
| `fb scan` / `fb watch` | ✅ | ✅ | ✅ |
| `fb service` (native service) | ✅ | ❌ use launchd | ❌ use systemd |
| Auto `flac` install | ✅ | manual | manual |

---

## Multiple input directories

Both `fb scan` and `fb watch` accept one or more input directories. Decoded files are placed into a single shared output (flat layout).

```bash
# Process two cache directories at once
fb scan ~/cache1 ~/cache2 --output ~/music

# Watch three cache directories continuously
fb watch ~/cache1 ~/cache2 ~/cache3 --output ~/music
```

### What happens in edge cases

| Scenario | Behaviour |
|---|---|
| `fb scan dir1 dir2` (both valid) | Both processed concurrently into shared output. |
| `fb scan dir1 missing` | Fails fast with non-zero exit; no work done. |
| `fb watch dir1 missing dir3` | Watches `dir1` + `dir3`; warns about `missing`; auto-retries it. |
| `dir1` deleted mid-watch | Its task pauses; other tasks unaffected. |
| `dir1` reappears | Auto-resumed within 5 minutes. |
| Same song cached in `dir1` + `dir2` | First writer wins; internal mutex prevents file races. |
| Ctrl-C / SIGTERM | All watch tasks drain gracefully; exit `130`. |

Each per-task log line is prefixed with `[watch=<basename>]` so you can tell which input directory produced each event.

---

## Integrity levels

| Level | What it checks | When to use |
|-------|----------------|-------------|
| `off` | No verification. Fastest. | You trust your inputs and want raw speed. |
| `l1`  | Structural check via TagLib# (header parse + duration > 0). Works for MP3, FLAC, M4A. | Default light check. |
| `l3`  | Full FLAC PCM-MD5 verification via external `flac -t`. **FLAC only** — falls back to `l1` for MP3 / M4A. | Maximum confidence for FLAC. Requires `flac` binary. |
| `auto` *(default)* | Probes `flac` at startup. Uses `l3` for FLAC if available, else `l1`. Always uses `l1` for MP3 / M4A. | Recommended for most users. |

If you use `--integrity l3` without `flac` on `PATH`, FreeBird exits with code `2` and a clear error message — it will not silently downgrade.

---

## Output layout

```
<output-dir>/
├── Artist - Title.flac            ← successfully decoded files
├── Another Artist - Song.mp3
├── .freebird-staging/             ← temporary; auto-cleaned on success
├── .freebird-failed/              ← quarantined files
│   ├── bad-song.flac
│   └── bad-song.flac.txt          ← sidecar with failure reason
└── .freebird/
    └── logs/
        └── watch-YYYY-MM-DD.log   ← watch mode log file
```

### Sidecar format

When integrity verification fails, FreeBird writes a small text file alongside the quarantined output:

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
| `1`  | One or more files failed integrity, had unknown format, or threw an error. |
| `2`  | Bad arguments (missing input dir, `--integrity l3` without `flac`, `--verbose` + `--quiet`, etc.). |
| `130`| Cancelled via Ctrl-C / SIGTERM. |

---

## How it works

For each `.uc` / `.uc!` file in the input directory:

1. **Decrypt** every byte with `XOR 0xA3` (streamed, low memory).
2. **Sniff** the first 12 bytes to identify MP3 / FLAC / M4A. Unknown format → quarantine.
3. **Atomically write** to `.freebird-staging/<guid>.<ext>`.
4. **Fetch metadata** from NetEase Cloud Music song-detail API to build the filename.
5. **Verify integrity** at the selected level.
6. If passed → atomic rename to `<output>/<Artist - Title>.<ext>`, then embed tags.
7. If failed → move to `.freebird-failed/` with a `.txt` sidecar capturing the reason.

Your input directory is never modified or deleted.

---

## Tips

- **No recursive scan** — only files directly in the input directory are processed. Pass each subdirectory you care about as a separate input argument.
- **Multi-byte CJK / Unicode** is fully supported in song names and artist names.
- **Multiple artists** in a song are joined with ` & ` in the filename and embedded as separate tag values.
- **Read-only on inputs** — FreeBird never modifies or deletes the source `.uc` files.

---

## Troubleshooting

| Problem | Try this |
|---|---|
| "flac binary not found" on Linux | `sudo apt install flac` (or your distro equivalent) |
| Windows auto-download fails | Check network access to `xiph.org`; or pass `--flac-bin C:\path\to\flac.exe` manually |
| Output files all named `<musicId>.flac` | NetEase API didn't respond (network issue). Re-run later. |
| Quarantine has files but I don't know why | `cat <output>/.freebird-failed/*.txt` — sidecars explain each failure |
| Watch mode keeps retrying a failed file | A quarantine sidecar exists. Delete it to retry: `rm <output>/.freebird-failed/foo.flac.txt` |

---

## Development

```bash
dotnet test     # runs the full test suite
dotnet build    # 0 warnings, 0 errors
```

### Project layout

| Project | Purpose |
|---------|---------|
| `src/FreeBird.Core` | XOR decoder, format sniffer, integrity checkers, file processor, watch supervisor |
| `src/FreeBird.Cli` | `Program.cs` entry, System.CommandLine wiring, Autofac container, Serilog logger |
| `src/FreeBird.Core.Tests` | Unit + small-integration tests for Core |
| `src/FreeBird.Cli.Tests` | End-to-end tests through `ScanRunner` / `WatchRunner` with real fixtures |

DI is wired via [Autofac](https://autofac.org/) using an `IDependency` marker-interface convention. Logging is [Serilog](https://serilog.net/) to console (and rolling file for watch mode).

For release history and migration notes, see [CHANGELOG.md](CHANGELOG.md).

---

## License

TBD.

### FLAC licensing acknowledgment

On Windows, FreeBird auto-downloads the Xiph FLAC binaries (`flac.exe`, `metaflac.exe`, `libFLAC.dll`, `libFLAC++.dll`) from xiph.org. These are distributed by the Xiph.Org Foundation under their own license terms: libFLAC is BSD-style, the command-line tools are GPL v2. FreeBird only invokes them as separate processes (no static linking) and ships no FLAC source code or binaries in this repository. If you redistribute FreeBird with the downloaded binaries bundled, please be aware of Xiph's licensing terms: https://xiph.org/flac/license.html
