# Bru — AI Screen Companion for Windows

> Bru sees your screen, hears your voice, answers out loud — and points to exactly what you asked about.

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
