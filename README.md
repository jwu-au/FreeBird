# FreeBird

One-sweep CLI to decrypt NetEase Cloud Music `.uc` / `.uc!` cache files (XOR `0xA3`) into clean MP3 / FLAC / M4A files, with optional audio-integrity verification.

> Cross-platform: macOS, Linux, Windows. Built on .NET 10.

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

---

## Install

### Build from source

Requires the **.NET 10 SDK** ([download](https://dotnet.microsoft.com/download/dotnet/10.0)).

```bash
git clone git@github.com:jwu-au/FreeBird.git
cd FreeBird
dotnet build -c Release
```

The CLI binary is produced at:

```
src/FreeBird.Cli/bin/Release/net10.0/fb        # macOS / Linux
src/FreeBird.Cli/bin/Release/net10.0/fb.exe    # Windows
```

You can copy it anywhere on your `PATH`, or invoke it directly via `dotnet run --project src/FreeBird.Cli`.

### Optional: install `flac` for full integrity verification

The L3 integrity level uses the official `flac` binary to decode every FLAC frame and verify the PCM-MD5 checksum stored in the file's `STREAMINFO` block — the gold-standard FLAC integrity check.

| OS | Install command |
|----|------|
| macOS | `brew install flac` |
| Linux (Debian/Ubuntu) | `sudo apt install flac` |
| Linux (Fedora) | `sudo dnf install flac` |
| Windows (Chocolatey) | `choco install flac` |
| Windows (Scoop) | `scoop install flac` |
| Windows (manual) | Download from [xiph.org/flac](https://xiph.org/flac/download.html) and add the folder to `PATH` |

If `flac` is not installed, FreeBird automatically falls back to L1 verification for FLAC files.

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

- **No NetEase metadata API integration** — output uses the original cache filename stem (e.g. `12345-abc.mp3`), not the song title.
- **No ID3 tag writing** — decoded files have the same tags as the original cache (which NetEase strips before caching).
- **`flac` binary must be on `PATH`** for L3 — no config-file override yet.
- **No recursive directory scan** — input directory is scanned non-recursively.

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
