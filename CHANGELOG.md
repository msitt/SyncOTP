# Changelog

All notable changes to SyncOTP are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versioning follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

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
