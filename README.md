# FreeBird

[![CI](https://github.com/jwu-au/FreeBird/actions/workflows/ci.yml/badge.svg)](https://github.com/jwu-au/FreeBird/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/jwu-au/FreeBird)](https://github.com/jwu-au/FreeBird/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Turn your **NetEase Cloud Music** offline files — streamed cache *and* downloaded songs — into clean, properly named, fully tagged **MP3 / FLAC / M4A** files you can play anywhere.

> Works on **macOS, Linux, and Windows**.

FreeBird takes the unplayable files NetEase leaves on your device and turns them into normal music files — named `Artist - Title.flac`, with proper tags, verified as not corrupted. It never touches your originals.

Two kinds of NetEase files are supported:

- **`.uc` / `.uc!` cache files** — what NetEase writes when you *stream* a song. FreeBird fetches the track's metadata from NetEase to name and tag the output.
- **`.ncm` files** — what NetEase writes when you *download* a song. These already contain the title, artist, album, and cover art inside the file, so FreeBird decodes them fully **offline** — no network needed — and embeds the album art automatically.

---

## Quick start

1. **Download** the latest build for your OS from the [releases page](https://github.com/jwu-au/FreeBird/releases) and extract it.
2. **Run one command:**

```bash
# Convert everything in a folder once, then exit:
fb scan <your-folder> --output ~/Music/decoded

# …or keep it running and auto-convert new files as they appear:
fb watch <your-folder> --output ~/Music/decoded
```

3. **Open** `~/Music/decoded` in your music player. That's it.

Point `<your-folder>` at your NetEase **stream cache** (for `.uc` / `.uc!` files) or at wherever you keep **downloaded** `.ncm` files — FreeBird handles both, and you can pass several folders at once.

> **Where's my stream cache folder?**
> - **macOS:** `~/Library/Containers/com.netease.163music/Data/Caches/online_play_cache`
> - **Windows:** `C:\Users\<you>\AppData\Local\Netease\CloudMusic\Cache\Cache`

---

## Install

Download the build for your system from the [releases page](https://github.com/jwu-au/FreeBird/releases), extract it, and (optionally) put `fb` somewhere on your `PATH`:

| OS | File |
|---|---|
| macOS (Apple Silicon) | `freebird-X.Y.Z-osx-arm64.tar.gz` |
| Linux | `freebird-X.Y.Z-linux-x64.tar.gz` |
| Windows | `freebird-X.Y.Z-win-x64.zip` |

<details>
<summary>Optional: the <code>flac</code> tool (for the strongest FLAC checks &amp; tags)</summary>

For full FLAC integrity verification and FLAC tag-writing, FreeBird uses the official `flac` tool.

| OS | How to get it |
|---|---|
| **macOS** | `brew install flac` |
| **Linux (Debian/Ubuntu)** | `sudo apt install flac` |
| **Linux (Fedora)** | `sudo dnf install flac` |
| **Linux (Arch)** | `sudo pacman -S flac` |
| **Windows** | **Automatic** — FreeBird downloads the official version the first time it's needed. No manual step. |

If you skip this on macOS/Linux, FreeBird still works — it uses a lighter integrity check and skips FLAC tag-writing. MP3 / M4A are unaffected.
</details>

<details>
<summary>Build from source</summary>

```bash
git clone https://github.com/jwu-au/FreeBird.git
cd FreeBird
dotnet build -c Release
```

The `fb` binary lands in `src/FreeBird.Cli/bin/Release/net10.0/`. See [CONTRIBUTING.md](CONTRIBUTING.md) for more.
</details>

---

## Usage

FreeBird has two main commands. Both take one or more input folders and a single `--output` folder.

Both commands automatically pick up NetEase `.uc` / `.uc!` cache files **and** `.ncm` downloaded files found directly in each input folder — no flags needed.

### `fb scan` — convert once and exit

```bash
fb scan <folder> --output ~/Music/decoded
```

### `fb watch` — keep converting new songs

Runs until you press Ctrl-C, converting files as they appear.

```bash
fb watch <folder> --output ~/Music/decoded
```

### Common options

| Option | Default | What it does |
|---|---|---|
| `-o, --output <dir>` | *(required)* | Where decoded files go. Created if missing. |
| `--integrity <level>` | `auto` | How thoroughly to verify audio. `auto` is recommended. |
| `--concurrency <n>` | `4` | How many files to process at once. |
| `--no-write-tags` | off | Don't embed ARTIST/TITLE/ALBUM tags. |
| `-v` / `-q` | off | More (`-v`) or less (`-q`) logging. |

> **Tip:** You can pass several folders at once — `fb scan ~/cache1 ~/cache2 --output ~/music`.

<details>
<summary>All <code>fb watch</code> options (polling, stability, log file)</summary>

`fb watch` accepts every `fb scan` option, plus:

| Option | Default | What it does |
|---|---|---|
| `--poll-interval <Ns\|Nm>` | `5s` | How often to check the folder (`1s`..`60m`). |
| `--stability-checks <n>` | `2` | Equal-size checks before a file is treated as finished (1-10). |
| `--min-file-size <bytes>` | `1024` | Ignore files smaller than this. |
| `--skip-initial-scan` | off | Only process files that appear *after* startup. |
| `--log-file <path>` | `<output>/.freebird/logs/watch-YYYY-MM-DD.log` | Where to write the watch log. |
| `--no-log-file` | off | Don't write a log file (console output is unaffected). |

The watch log rolls daily and keeps 14 days. Each line is prefixed with `[watch=<folder>]` so you can tell which input produced it.
</details>

<details>
<summary>Integrity levels explained</summary>

| Level | What it checks | When to use |
|---|---|---|
| `off` | Nothing. Fastest. | You trust your inputs. |
| `l1`  | Quick structural check (works for MP3, FLAC, M4A). | Light, dependency-free. |
| `l3`  | Full FLAC verification via the `flac` tool. **FLAC only.** | Maximum confidence for FLAC. |
| `auto` *(default)* | Uses `l3` for FLAC if the `flac` tool is available, otherwise `l1`. | Recommended for most people. |

If you ask for `l3` without the `flac` tool installed, FreeBird stops with a clear error rather than silently downgrading.
</details>

<details>
<summary>Advanced / scripting options</summary>

| Option | Env var | Purpose |
|---|---|---|
| `--flac-bin <path>` | — | Force a specific `flac` location. |
| `--no-auto-download` | `FREEBIRD_NO_AUTO_DOWNLOAD=1` | Disable Windows auto-download of `flac`. |
| `--flac-url <url>` | `FREEBIRD_FLAC_URL` | Override the download source (mirrors / air-gapped). |

**Windows pre-warm:** `fb install-flac [--target <dir>] [--url <url>]` downloads the `flac` tool ahead of time (handy for CI / scripted installs). On macOS/Linux it just prints a hint to use your package manager.
</details>

---

## Where your files go

```
~/Music/decoded/
├── Artist - Title.flac          ← your converted music
├── Another Artist - Song.mp3
└── .freebird-failed/            ← anything that couldn't be verified, with a .txt note explaining why
```

**To re-convert a file**, just delete its output and (in watch mode) wait for the next cycle — FreeBird notices it's missing and decodes the source again. To retry a failed file, delete its `.txt` note in `.freebird-failed/`.

---

## Running in the background

Want FreeBird to keep converting automatically across reboots? You can run `fb watch` under your OS's service manager.

### Windows (native service)

FreeBird ships a built-in Windows Service. Run these in an **Administrator PowerShell**:

```powershell
fb service init                                       # 1. create a default config
notepad C:\ProgramData\FreeBird\config.json           # 2. set your cache + output folders
fb service install --config C:\ProgramData\FreeBird\config.json   # 3. register it
fb service start                                       # 4. start it
fb service status                                      # 5. confirm it's running
```

Other commands: `fb service stop`, `restart`, `uninstall`.

<details>
<summary>Windows Service details (accounts, logs, troubleshooting)</summary>

**Reading user-profile caches.** The default service account (`LocalSystem`) can't read user-profile paths like `%LocalAppData%\NetEase\...`. If your cache lives there, install with `--service-account <DOMAIN\user>` (a **gMSA** is recommended) so the service runs as an identity that can read those files. Provide the password via `--service-password` or the `FB_SERVICE_PASSWORD` environment variable.

**Logs.** The service writes a full daily log to `%ProgramData%\FreeBird\logs\watch-YYYY-MM-DD.log`. The Windows **Event Log** (source `FreeBird`, Application log) records only **errors** — so routine warnings don't clutter Event Viewer. To diagnose a warning, check the file log.

**Applying config changes.** Edit the config, then run `fb service restart`.

**Exit codes** (for scripting): each subcommand returns `0` on success and documented non-zero codes per failure class (e.g. `install`: `1` not admin, `2` already installed, `3` config invalid, `4` SCM error).
</details>

<details>
<summary>macOS (launchd) &amp; Linux (systemd)</summary>

FreeBird's native service is Windows-only, but `fb watch` is a normal long-running process any init system can supervise.

**macOS — launchd** (`~/Library/LaunchAgents/com.freebird.watch.plist`):

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>com.freebird.watch</string>
    <key>ProgramArguments</key>
    <array>
        <string>/usr/local/bin/fb</string>
        <string>watch</string>
        <string>/Users/me/NetEaseCache</string>
        <string>-o</string>
        <string>/Users/me/Music/FreeBird</string>
    </array>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key><true/>
</dict>
</plist>
```

Load it: `launchctl load ~/Library/LaunchAgents/com.freebird.watch.plist`.

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

Enable it: `sudo systemctl enable --now freebird`.
</details>

---

## Troubleshooting

| Problem | Try this |
|---|---|
| A file is named `<number>.mp3` instead of `Artist - Title.mp3` | Only happens for `.uc` cache files: metadata wasn't available yet (no network, or NetEase rate-limited the request). FreeBird retries automatically and renames it once it succeeds. (`.ncm` downloads carry their own metadata, so they never fall back to a number.) |
| `flac` not found (Linux) | `sudo apt install flac` (or your distro's equivalent). |
| Windows auto-download of `flac` fails | Check network access to `xiph.org`, or pass `--flac-bin C:\path\to\flac.exe`. |
| Something landed in `.freebird-failed/` | Open the matching `.txt` note next to it — it explains the failure. |
| Watch keeps retrying a failed file | Delete its `.txt` note in `.freebird-failed/` to retry. |

<details>
<summary>Top-level exit codes (for scripting)</summary>

| Code | Meaning |
|---|---|
| `0`  | All files decoded successfully (or input was empty). |
| `1`  | One or more files failed integrity, had an unknown format, or errored. |
| `2`  | Bad arguments (missing input, `l3` without `flac`, `-v` + `-q`, etc.). |
| `130`| Cancelled via Ctrl-C / SIGTERM. |
</details>

<details>
<summary>Good to know</summary>

- **No recursive scan** — only files directly in each input folder are processed. Pass subfolders as separate arguments.
- **Read-only on inputs** — FreeBird never modifies or deletes your source `.uc` / `.ncm` files.
- **CJK / Unicode** song and artist names are fully supported.
- **Multiple artists** are joined with ` & ` in the filename and stored as separate tag values.
- **Same song in two folders** — the first writer wins; an internal lock prevents file races.
</details>

---

## Contributing

Bug reports, ideas, and pull requests are welcome. See **[CONTRIBUTING.md](CONTRIBUTING.md)** for how to build, test, and navigate the codebase, and [CHANGELOG.md](CHANGELOG.md) for release history.

---

## License

Released under the [MIT License](LICENSE) — © 2026 FreeBird Contributors.

<details>
<summary>FLAC licensing acknowledgment</summary>

On Windows, FreeBird auto-downloads the Xiph FLAC binaries (`flac.exe`, `metaflac.exe`, `libFLAC.dll`, `libFLAC++.dll`) from xiph.org. These are distributed by the Xiph.Org Foundation under their own terms: libFLAC is BSD-style, the command-line tools are GPL v2. FreeBird invokes them as separate processes (no static linking) and ships no FLAC source or binaries in this repository. If you redistribute FreeBird with these binaries bundled, please review Xiph's terms: https://xiph.org/flac/license.html
</details>
