# Contributing to FreeBird

Thanks for your interest in improving FreeBird! This document covers everything you
need to build, test, and understand the codebase. For day-to-day usage, see the
[README](README.md).

---

## Building & testing

```bash
git clone https://github.com/jwu-au/FreeBird.git
cd FreeBird

dotnet build -c Release   # must be 0 warnings, 0 errors
dotnet test  -c Release   # full unit + end-to-end suite
```

The `fb` (or `fb.exe`) binary lands in `src/FreeBird.Cli/bin/Release/net10.0/`.

- **Runtime / language:** C# on .NET 10.
- **Zero-warning policy:** `dotnet build -c Release` must report 0 warnings.
- **Tests must pass on all three OSes** (macOS, Linux, Windows). CI runs the full
  matrix on every push and pull request.

---

## Project layout

| Project | Purpose |
|---------|---------|
| `src/FreeBird.Core` | XOR decoder, format sniffer, integrity checkers, file processor, watch supervisor, NetEase metadata, tag writers |
| `src/FreeBird.Cli` | `Program.cs` entry, System.CommandLine wiring, Autofac container, Serilog logger, Windows Service support |
| `src/FreeBird.Core.Tests` | Unit + small-integration tests for Core |
| `src/FreeBird.Cli.Tests` | End-to-end tests through `ScanRunner` / `WatchRunner` with real fixtures |

---

## Architecture notes

- **Dependency injection** is wired via [Autofac](https://autofac.org/) using an
  `IDependency` marker-interface convention (auto-registration via reflection scan
  in `CoreModule.cs`). Stateful singletons (rate limiters, mutex pools, supervisors)
  get explicit `SingleInstance()` carve-outs.
- **Logging** is [Serilog](https://serilog.net/): console sink always, plus a
  rolling daily file sink for watch and service modes.
- **Decryption** XORs every byte with `0xA3` (NetEase's obfuscation), streamed for
  low memory use.
- **Format sniffing** matches magic bytes in the first 12 bytes (MP3 ID3 / MP3 sync
  / FLAC `fLaC` / M4A `ftyp`); unknown formats are quarantined.
- **Atomic writes:** output goes to `.freebird-staging/<guid>.<ext>` then
  `File.Move(..., overwrite: true)` — the final filename is never written directly.
- **Failures** are quarantined to `.freebird-failed/` with a key=value `.txt`
  sidecar. The sidecar contract is a stability promise: the watch loop reads it to
  skip permanently-failed files, so fields must not be renamed or dropped without a
  migration plan.

For the full set of settled architectural decisions and coding conventions, see
[`AGENTS.md`](AGENTS.md).

---

## Coding conventions

- Brackets always with control-flow statements, even single-line `if`.
- Import from specific files, not barrel files.
- Descriptive names, no acronyms (`FlacBinaryResolver`, not `FBR`).
- `Async` suffix on async methods; pass `CancellationToken ct` through everywhere.
- Don't remove code or comments you don't understand — investigate first.

---

## Pull requests

1. Build and test locally (`dotnet build -c Release && dotnet test -c Release`).
2. Keep the build at 0 warnings.
3. Follow the existing commit style: `<type>(<scope>): <subject>` where type is one
   of `feat`, `fix`, `refactor`, `test`, `docs`, `chore`, `ci`.
4. CI must be green on all three OSes before merge.

For release history and migration notes, see [CHANGELOG.md](CHANGELOG.md).
