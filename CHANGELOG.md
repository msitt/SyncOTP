# Changelog

All notable changes to SyncOTP are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versioning follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- SyncOTP checks GitHub for a newer release shortly after startup and once a day after that, and
  offers it from the tray menu. Choosing it downloads the release matching your install, verifies
  it, and swaps it in when you restart. Config and logs are untouched, and the previous version is
  kept in `%LOCALAPPDATA%\SyncOTP\updates\backup` in case the new one will not start
- New `updates` block in `config.json` to control all of that, including turning it off entirely
- Releases now publish a `SHA256SUMS.txt`, and each zip carries a `release.json` recording which
  build it is
- `SyncOTP.exe --version` prints the version and exits

### Fixed

- Exiting from the tray menu no longer crashes with a NullReferenceException while the ntfy
  source reports itself stopped

## [0.1.1] - 2026-09-09

### Added

- App version is now shown at startup (log) and as a non-clickable entry at the top of the tray
  menu

### Fixed

- Logged exceptions now include the full stack trace

## [0.1.0] - 2026-09-07

### Added

- Initial release: tray app that watches an ntfy topic, extracts SMS verification codes, and copies
  them to the clipboard with a toast notification.
