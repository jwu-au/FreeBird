# AGENTS.md — FreeBird

Repo-level instructions for AI coding agents. Read this **before** touching any code.

---

## 1. What FreeBird is

A CLI that decrypts NetEase Cloud Music cache files (`.uc` / `.uc!`) into playable MP3 / FLAC / M4A, fetches song metadata from NetEase to produce proper filenames + audio tags, and verifies audio integrity.

Two commands: `fb scan` (one-shot) and `fb watch` (continuous). Plus a Windows-only helper `fb install-flac`.

User-facing docs live in `README.md`. Release history lives in `CHANGELOG.md`. **Do not duplicate either of those into this file.**

---

## 2. Project at a glance

| Item | Value |
|---|---|
| Language / runtime | C# / .NET 10 |
| Solution file | `FreeBird.sln` |
| Projects | `FreeBird.Core`, `FreeBird.Cli`, `FreeBird.Core.Tests`, `FreeBird.Cli.Tests` (all under `src/`) |
| CI | GitHub Actions: `ci.yml` (PR + push, 3 OS) + `release.yml` (tag-triggered) |
| Supported OS | macOS (arm64), Linux (x64), Windows (x64) |
| Latest release | `git tag --sort=-creatordate | head -1` |

---

## 3. Architecture — settled decisions

These were debated and chosen for specific reasons. If you think one is wrong, raise it explicitly before changing.

- **Decryption**: XOR every byte with `0xA3` (NetEase's actual obfuscation; verified against real cache files). Streamed, not buffered.
- **Format sniffing**: First 12 bytes, magic-byte matching (MP3 ID3 / MP3 sync / FLAC `fLaC` / M4A `ftyp`). Unknown → quarantine.
- **Integrity check**: 4 levels — `off`, `l1` (TagLibSharp structural), `l3` (FLAC `flac -t` PCM-MD5 subprocess), `auto` (probe `flac` at startup, use l3 if available else l1).
- **Atomic writes**: All output goes through `<output>/.freebird-staging/<guid>.<ext>` then `File.Move(..., overwrite: true)`. Never write the final filename directly.
- **Failures**: Quarantine to `<output>/.freebird-failed/<stem>.<ext>` plus a `.txt` sidecar (key=value format) with `timestamp`, `source`, `source_size`, `source_mtime`, `format`, `integrity`, `reason`, `error_class`.
  - **The sidecar contract is a stability promise** — the watch loop reads it to skip permanently-failed files. Don't rename or drop fields without a migration plan.
- **DI**: Autofac with `IDependency` marker-interface convention (auto-registration via reflection scan in `CoreModule.cs`). Stateful singletons (rate limiters, mutex pools, supervisors) need explicit `SingleInstance()` carve-outs.
- **Service contracts (v3.5, Cli-side)**: `IServiceController`, `IElevationChecker`, `IEventLogWriter`, `ILogPathResolver` are registered in `CliServiceModule` (src/FreeBird.Cli/DependencyInjection) with OS-appropriate impls selected via `OperatingSystem.IsWindows()` (Windows* vs NotSupported*/NonWindows*); all `InstancePerLifetimeScope` (NOT singletons — they hold no shared state). `IConfigLoader` (FreeBird.Core.Service) is the one service contract that does NOT extend `IDependency` and is constructed explicitly (`new JsonConfigLoader(logger)`), not auto-scanned.
- **Logging**: Serilog. Console sink always; rolling file sink for watch mode (daily rotation, 14-day retention).
- **Naming**: Files are named `<artist> - <title>.<ext>` from NetEase API metadata; falls back to `<musicId>.<ext>` when API fails. Multi-artist joined with ` & ` in filename.
- **Tag writing**: `metaflac` subprocess for FLAC (preserves PCM-MD5); TagLibSharp for MP3/M4A. On by default; opt-out via `--no-write-tags`. Preserves existing unrelated tags (GENRE, DATE, etc).
- **CLI parsing**: System.CommandLine. Multi-input args use `Argument<List<string>>` with `ArgumentArity.OneOrMore`.

---

## 4. Dev workflow

### Before any change

1. `git log --oneline -20` — what's been touched recently
2. `dotnet build -c Release` — confirm baseline is green
3. `dotnet test -c Release` — confirm baseline test count

### TDD is the default

This codebase was built with strict TDD throughout. Every behaviour change must follow RED → GREEN → REFACTOR.

1. Write failing test first; run; observe RED.
2. Make minimal change to pass; run; observe GREEN.
3. Refactor if needed; run; still GREEN.
4. Commit.

If a test never shows RED, it isn't real TDD coverage — temporarily revert the production change and re-run to prove the test would have caught the bug.

### Build & test discipline

- **Zero-warning policy.** `dotnet build -c Release` must show 0 warnings. If you add one, fix it.
- **All tests must pass.** A genuinely platform-specific test should use `[Trait]` + `Skip.If(...)` with a precise reason — never silently delete a failing test.
- **No build artifacts committed.** `.gitignore` covers `bin/`, `obj/`, `*.tsbuildinfo`. Double-check after pre-commit hooks run.

### Commits

- Verify commits actually landed: `git log --oneline -1 && git status`. Pre-commit hooks can fail silently.
- Format: `<type>(<scope>): <subject>` where type is one of `feat`, `fix`, `refactor`, `test`, `docs`, `chore`, `ci`. Existing `git log` shows the established style.
- Multi-line messages are welcome for non-trivial changes.

---

## 5. Code style

- **Brackets always with control flow.** Even single-line `if`.
- **Import from specific files**, never barrel files.
- **Descriptive names, no acronyms.** `FlacBinaryResolver`, not `FBR`.
- **Destructured object parameters** for functions with ≥3 params when sensible.
- **Don't remove code or comments you don't understand.** Ask or investigate first.
- **Async**: `Async` suffix on async methods. `CancellationToken ct` is the conventional name. Pass it through everywhere.
- **DI lifetime**: Default is `InstancePerLifetimeScope`. Use explicit `SingleInstance()` carve-outs in `CoreModule.cs` only when the type holds shared state.
- **Errors**: Never silently swallow. Either propagate, or log with `_log.Warning(...)` / `_log.Error(...)` and describe what was lost.

---

## 6. Critical gotchas (learned the hard way)

### Static fields are test-pollution landmines

`ScanRunner.RunnerOverride`, `WatchRunner.OrchestratorFactoryOverride`, `WatchRunner.CoordinatorFactoryOverride`, `WatchCommand.HandlerOverride`, `InstallFlacRunner.ContainerOverride` are all `public static` fields used as test hooks. xUnit runs different test classes in parallel by default → state leaks across classes.

**Rule**: any test class that mutates one of these MUST carry `[Collection("GlobalStaticState")]` (definition in `src/FreeBird.Cli.Tests/GlobalStaticStateCollection.cs`). If you add a new static test hook, add it to that collection's doc and tag every caller.

### macOS vs Linux test scheduling differs

macOS happens to schedule tests in an order that hides certain race conditions. **Always check CI ubuntu output before declaring a fix complete.** Local-only `dotnet test` on macOS is necessary but not sufficient.

### Output-path mutex serialises Steps 8-9 of FileProcessor

Same-musicId-different-bitrate inputs can produce the same output filename. `OutputPathMutexPool` serialises the whole skip-check → atomic move → tag-write block. **Don't move work out of that mutex.**

### Windows `flac.exe` auto-download path

The path inside the official Xiph ZIP starts with `flac-1.5.0-win/Win64/...` — the full prefix is required. See `src/FreeBird.Cli/Provisioning/WindowsFlacAutoInstaller.cs`. The SHA-256 of the official ZIP is pinned in source; **do not change unless you verify against xiph.org**.

---

## 7. Release workflow

1. All changes go to `main` (this repo doesn't use feature branches).
2. CI runs on push + PR; `release.yml` runs on tag push matching `v*.*.*`.
3. To ship:
   - Add a CHANGELOG entry at the top (above the previous version, follow existing format).
   - Bump `<Version>` in `src/FreeBird.Cli/FreeBird.Cli.csproj` (and any other csproj that carries it).
   - Commit + push.
   - `git tag -a vX.Y.Z -m "FreeBird vX.Y.Z: <title>\n\n<body>"` (annotated, multi-line).
   - `git push origin vX.Y.Z` → triggers `release.yml` → builds three OS binaries → creates a **draft** release.
   - Verify with `gh release list` and `gh release view vX.Y.Z`.
   - Publish: `gh release edit vX.Y.Z --draft=false`.

`release.yml` auto-extracts release notes from CHANGELOG via `awk` — keep the CHANGELOG format consistent or release notes will silently come out empty.

---

## 8. Don't do these things

- ⚠️ **Prefer CLI flags + env vars for `fb scan` and `fb watch`.** A config file may be appropriate for service-mode installation (`fb service`) where the same long argument list is invoked at every boot and stored externally — that is an accepted exception, not a license to add JSON/YAML to the scan/watch commands.
- ❌ **Don't introduce a third-party FLAC decoding library.** Use the official `flac` subprocess.
- ❌ **Don't bundle FLAC binaries in this repo.** They're downloaded at runtime from xiph.org (Windows) or installed by the user (macOS/Linux).
- ❌ **Don't modify input files.** FreeBird is read-only on inputs.
- ❌ **Don't break the sidecar contract** without a migration plan.
- ❌ **Don't commit build artifacts** (`bin/`, `obj/`, `*.tsbuildinfo`, `dist/`). Already in `.gitignore`; check after pre-commit hooks run.
- ❌ **Don't auto-publish releases.** Always create as draft first, verify artifacts, then `--draft=false`.
- ❌ **Don't post on Confluence / Jira / PR comments without explicit user approval.** Draft, present, wait.

---

## 9. When unsure

Ask. A 30-second question to the user is cheaper than minutes of speculation. Especially:

- "Should this be a patch (X.Y.Z+1) or a minor (X.Y+1) feature?"
- "Should this BREAKING change get a major bump?"
- "Do you want me to push, or just commit?"
- "Is the failing test a real bug or a flaky test?"

Honest reporting beats fake confidence. If you tried something and it didn't work, say so. If you don't know whether something is true, say "I think X but haven't verified."
