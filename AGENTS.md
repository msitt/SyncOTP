# AGENTS.md

Guidance for agents (and humans) working in this repository.

## What this is

SyncOTP is a Windows tray app. An iPhone Shortcut forwards incoming SMS to an ntfy topic, the app
subscribes to that topic, extracts a verification code from whatever arrives, and puts it on the
clipboard with a toast notification. See [README.md](README.md) for the full user-facing behavior
(clipboard handling, staleness, dedup rules, config schema), that's not repeated here.

## Commands

```powershell
dotnet build
dotnet test
dotnet run --project src\SyncOTP.App
```

Run a single test class or method with xunit's filter:

```powershell
dotnet test --filter "FullyQualifiedName~CodeExtractorTests"
dotnet test --filter "FullyQualifiedName~CodeExtractorTests.SomeSpecificTest"
```

CI (`dotnet restore --locked-mode`) requires the `packages.lock.json` files to match `*.csproj`
exactly. After changing a `PackageReference`, run `dotnet restore` (not `--locked-mode`) once to
regenerate the lock file, then commit it alongside the csproj change.

To build and install a local copy (compiles from source, replaces the running instance, preserves
config/logs):

```powershell
.\scripts\install.ps1 -Startup
```

## Architecture

Two projects plus tests, referenced from [SyncOTP.slnx](SyncOTP.slnx):

- **`src/SyncOTP.Core`** holds extraction, dedup, config, logging, and paths. No UI, no I/O beyond
  the filesystem, fully unit-testable. `CodeExtractor` is the core algorithm: mask spans that are
  definitely not codes (phone numbers, prices, times, order numbers, URLs, carrier boilerplate),
  then score what survives by pattern quality and proximity to a verification keyword. It's
  deliberately dependency-free and covered by a large corpus of real message shapes in
  `tests/SyncOTP.Core.Tests/CodeExtractorTests.cs`. When a message from some service is missed,
  the fix is almost always "add it to the corpus, then adjust the extractor."
- **`src/SyncOTP.App`** is the WinForms tray shell (`TrayContext` is the whole app: tray icon,
  menu, and the pipeline wiring). `Sources/` holds message transports behind `IMessageSource`
  (`NtfySource` is the only implementation), so a second transport is a new class plus a config
  block, not a change to the pipeline. `Output/` holds `ClipboardService` (marshals onto a hidden
  control's UI thread, flags clipboard entries out of history/roaming), `Notifier` (toast, falls
  back to a tray balloon), and `CodeHistory` (last five codes for the tray menu).

Pipeline, end to end: `NtfySource` receives a message, `TrayContext.OnMessage` marshals it onto
the UI thread, `MessageDeduper` filters repeats, `CodeExtractor` pulls a code (or bails), a
staleness check (age vs. `clipboard.staleAfterSeconds`) decides whether it touches the clipboard,
`ClipboardService` copies it and schedules the auto-clear, `Notifier` shows the toast, and
`CodeHistory` records it for the tray menu.

Config lives at `%APPDATA%\SyncOTP\config.json` (`Config.Load`/`Save` in `SyncOTP.Core`). Logs
live at `%LOCALAPPDATA%\SyncOTP\logs\` (`FileLog`). Both paths come from `Paths` in
`SyncOTP.Core`, not hardcoded elsewhere. `Config.SaveLastId` re-reads the on-disk file before
patching just the resume cursor, specifically so it doesn't clobber edits made by hand while the
app is running. Don't "simplify" that into a plain field write.

`AppVersion.Display` reads the informational version baked in at build time from `<Version>` in
[Directory.Build.props](Directory.Build.props). Bump that file, not a hardcoded string, when
changing the app version (see Release process below).

## Conventions

- It is acceptable to commit directly to `master`, no PR workflow required. Follow
  [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `chore:`, `ci:`,
  `docs:`, etc.) for commit subjects.
- `CHANGELOG.md` follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Add an entry
  under `## [Unreleased]` only for changes a user would notice (new behavior, config keys, fixed
  bugs). Skip it for internal refactors, CI tweaks, or doc-only changes.
- No em-dashes anywhere, in code, comments, docs, or commit messages. Use a period, comma, or
  parentheses instead.
- Don't join two complete sentences with a semicolon. Use a period and start a new sentence, or
  restructure with a conjunction.

## Release process

1. **Roll the changelog.** Rename `## [Unreleased]` to `## [<version>] - <YYYY-MM-DD>` and start a
   fresh empty `## [Unreleased]` above it, matching the existing `CHANGELOG.md` format.
2. **Roll the version.** Bump `<Version>` in [Directory.Build.props](Directory.Build.props) to the
   same `<version>`.
3. **Commit and tag.** Commit both changes, then `git tag v<version>` and `git push --tags` (or
   push the commit and tag together). Pushing a `v*` tag triggers
   [`.github/workflows/release.yml`](.github/workflows/release.yml), which builds both the
   self-contained and framework-dependent artifacts, extracts that version's section from
   `CHANGELOG.md` for the release body, and publishes the GitHub Release with both zips attached.

The tag's version must exactly match the `Directory.Build.props` version and the `CHANGELOG.md`
heading. The release job derives the version from the tag alone and looks up the changelog section
by that same string.
