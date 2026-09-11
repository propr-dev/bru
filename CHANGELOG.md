# Changelog

All notable changes to Bru are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Bru is a fork of [clicky_windows](https://github.com/emreyilmaz46/clicky_windows) by
emreyilmaz46, itself a Windows port of [Clicky](https://github.com/farzaa/clicky) by
Farzaa. Entries before 1.0.0 describe the upstream work this fork inherited.

## [Unreleased]

### Added
- Continuous integration: Release build plus a single-file publish check on every push
  and pull request, with warnings treated as errors
- Tagged-release workflow that packages the self-contained executable as a downloadable
  `.zip` attached to a GitHub Release
- `CONTRIBUTING.md`, `SECURITY.md`, and `CODE_OF_CONDUCT.md`
- Structured issue forms for bugs and feature requests, and a pull request template
- `.editorconfig` encoding the existing code conventions
- `.gitattributes` for consistent line-ending handling
- Dependabot for NuGet packages and GitHub Actions

### Changed
- Upstream attribution now appears at the top of the README rather than only in Credits
- `LICENSE` carries a copyright line for this fork's additions alongside the original

### Security
- `.gitignore` now excludes `settings.json`, `.env` files, and `*.log`, so a settings file
  holding live API keys cannot be committed by accident

## [1.0.0] — 2026-07-03

The release that turned the Clicky Windows port into Bru.

### Added
- **File assistant** — list, find, and copy files by voice, restricted to the directories
  in `AllowedFolders`, with per-action approval in the Bru window. No move, no delete.
- **Switchable voice engine** behind `ITtsService`: free offline Windows SAPI, or
  ElevenLabs for a custom voice
- **The Bru window** — a dark panel with a "living core" that breathes and pulses with
  state, the latest exchange, a stop-speech control, and permission prompts
- **Split model configuration** — a cheaper `ClaudeModel` for conversation and a stronger
  `PointingModel` for Computer Use aiming
- **Optional proxy settings** (`ClaudeProxyUrl`, `ElevenLabsProxyUrl`,
  `AssemblyAiTokenUrl`) so API keys need not sit on the client machine
- Conversation memory across the last 10 turns

### Changed
- Rebuilt the pointing pipeline as two passes — a coarse locate followed by a
  native-resolution zoom — substantially improving accuracy
- New identity, naming, and visual design throughout
- Positioned for the South African market: a free offline voice option and a cheaper AI
  mode, with local-language support on the roadmap

## Upstream history

### [clicky_windows] — 2026-04-10
- README, MIT `LICENSE`, and `settings.example.json` added by emreyilmaz46

### [clicky_windows] — 2026-04-09
- Initial Windows port of Clicky by emreyilmaz46: voice interaction, screen awareness,
  and the blue locating dot
- Overlay feedback and spinner animation improvements

### [Clicky]
- The original macOS project by [Farzaa](https://github.com/farzaa), from which all of
  the above descends

[Unreleased]: https://github.com/propr-dev/bru/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/propr-dev/bru/releases/tag/v1.0.0
