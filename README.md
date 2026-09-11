# Bru — AI Screen Companion for Windows
[![Build](https://github.com/propr-dev/bru/actions/workflows/build.yml/badge.svg)](https://github.com/propr-dev/bru/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform: Windows](https://img.shields.io/badge/platform-Windows%2011-0078D4?logo=windows&logoColor=white)](#requirements)

> Bru sees your screen, hears your voice, answers out loud — and points to exactly what you asked about.

*A fork of [clicky_windows](https://github.com/emreyilmaz46/clicky_windows) by emreyilmaz46, itself a Windows port of [Clicky](https://github.com/farzaa/clicky) by Farzaa. See [Credits](#credits) for what is theirs and what this fork adds.*

Hold **Ctrl + Space**, speak, release. Bru captures your screen, transcribes your voice in real time, sends both to Claude, then speaks the answer back and flies a glowing cyan dot to the exact UI element you asked about. It can also find and copy files for you — but only inside folders you allow, and only after you approve each action.

Built for the South African market: a free offline voice option, a cheaper AI mode, and (on the roadmap) local-language support.

## Features

- **Screen vision** — understands whatever is on screen: apps, errors, documents, websites
- **Precision pointing** — two-pass Computer Use aiming (coarse locate → native-resolution zoom) flies the dot to the exact element
- **Voice in & out** — push-to-talk speech recognition in, natural voice out
- **Switchable voice engine** — free Windows SAPI voice (offline) or ElevenLabs (e.g. a custom JARVIS voice), via `TtsEngine`
- **File assistant** — list/find and copy files by voice; folder-scoped, Allow/Deny approval per action, no move, no delete
- **The Bru window** — a small dark panel with a "living core" that breathes/pulses per state, the latest exchange, a stop-speech button, and permission approvals
- **Conversation memory** — remembers the last 10 turns
- **Split models** — cheap model for chat (`ClaudeModel`), strong model for pointing (`PointingModel`)

## Requirements

| | |
|---|---|
| **OS** | Windows 10 build 22000+ or Windows 11, x64 |
| **Install** | None — one self-contained `ClickyWindows.exe` (~180 MB, the .NET runtime is bundled) |
| **Hardware** | A microphone |
| **Keys** | An [Anthropic](https://console.anthropic.com/) key and an [AssemblyAI](https://www.assemblyai.com/) key |
| **Cost** | Pay-as-you-go per interaction. `TtsEngine: "windows"` keeps voice output free. |

## Setup

1. Install nothing — Bru ships as one self-contained `ClickyWindows.exe`.
2. Copy `settings.example.json` to `%APPDATA%\ClickyWindows\settings.json` and fill in:
   - `AnthropicApiKey` — [console.anthropic.com](https://console.anthropic.com/)
   - `AssemblyAiApiKey` — [assemblyai.com](https://www.assemblyai.com/)
   - (optional) `ElevenLabsApiKey` + `ElevenLabsVoiceId` with `TtsEngine: "elevenlabs"`
3. Run the exe. A tray icon and the Bru window appear. Hold **Ctrl + Space** and talk.

**Never commit real API keys.** The source defaults are empty on purpose; keys live only in your local settings file.

## Settings reference

| Field | Default | Meaning |
|---|---|---|
| `ClaudeModel` | `claude-sonnet-4-6` | Conversation model (set `claude-haiku-4-5` for cheapest) |
| `PointingModel` | `claude-sonnet-4-6` | Model for the precise pointing call only |
| `TtsEngine` | `windows` | `windows` (free/offline) or `elevenlabs` |
| `WindowsTtsVoice` | — | Name (or part) of an installed Windows voice; options are logged at startup |
| `HotkeyModifiers` / `HotkeyVirtualKey` | `2` / `32` | Ctrl + Space (Win32 modifier flags / virtual-key code) |
| `FileAccessEnabled` | `true` | Master switch for the file tools |
| `AllowedFolders` | — | The only folders Bru may list/copy inside |

Logs: `%APPDATA%\ClickyWindows\clicky.log`.

## What Bru sends, and where

Bru sees your screen and hears your voice, so it is worth being precise about this.

| Service | Receives | When |
|---|---|---|
| Anthropic | Screenshots, your transcribed speech, conversation history | Every request |
| AssemblyAI | Microphone audio | Every request |
| ElevenLabs | Answer text | Only when `TtsEngine` is `elevenlabs` |

**Whatever is on screen when you press the hotkey is sent** — Bru does not blur or redact
anything. Keys are stored unencrypted in `%APPDATA%\ClickyWindows\settings.json`, and
`clicky.log` can contain what you said and what you were looking at.

Use a spend-limited API key, keep `AllowedFolders` narrow, and read
[SECURITY.md](./SECURITY.md) before running this on a machine that holds anything sensitive.

## Architecture

```
Services/
  CompanionManager    orchestrates: hotkey → audio → ASR → Claude → dot + voice
  AudioCaptureService WASAPI mic capture → PCM16 16 kHz mono
  AssemblyAIService   streaming speech-to-text (v3 WebSocket, pre-connect buffering)
  ClaudeService       conversation + file tool-use loop + two-pass Computer Use pointing
  FileService         folder-scoped list/copy with staged approval
  WindowsTtsService / ElevenLabsService   switchable TTS behind ITtsService
OverlayWindow         fullscreen click-through overlay: spring-physics dot, waveform, spinner
StatusWindow          the Bru panel: living core, latest exchange, permissions
```

## Credits

Bru is a heavily customized fork of [clicky_windows](https://github.com/emreyilmaz46/clicky_windows) by emreyilmaz46, itself a Windows port of [Clicky](https://github.com/farzaa/clicky) by Farzaa. The concept and original pipeline are theirs; the Bru identity, UI, file tools, voice engine switch, pointing overhaul, and fixes are this fork's. MIT licensed.

## Roadmap

- [ ] Local-language support — isiZulu, isiXhosa, and Afrikaans voice in and out
- [ ] Reduce end-to-end latency between release of the hotkey and first spoken word
- [ ] Pointing accuracy across mixed-DPI multi-monitor setups
- [ ] Signed releases so Windows SmartScreen stops warning on first run

Ideas and pull requests welcome — see [CONTRIBUTING.md](./CONTRIBUTING.md).

## Contributing

Contributions are welcome. [CONTRIBUTING.md](./CONTRIBUTING.md) covers the build, how the
code is laid out, the conventions to follow, and what kind of change is likely to land.

Bear in mind that Bru has a deliberately narrow surface — it reads the screen, listens,
answers, points, and copies files inside folders you approve. It never moves or deletes
anything. That restraint is a feature.

| | |
|---|---|
| 🐛 Found a bug? | [Open an issue](https://github.com/propr-dev/bru/issues/new?template=bug_report.yml) |
| 💡 Have an idea? | [Request a feature](https://github.com/propr-dev/bru/issues/new?template=feature_request.yml) |
| 🔒 Security problem? | **Do not open an issue** — see [SECURITY.md](./SECURITY.md) |
| 📋 What changed? | [CHANGELOG.md](./CHANGELOG.md) |
| 🤝 Community standards | [CODE_OF_CONDUCT.md](./CODE_OF_CONDUCT.md) |

## Licence

[MIT](./LICENSE) — copyright the original author (EmreYZ) and this fork's contributors.
