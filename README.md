# Clicky for Windows

> An AI screen companion that sees your screen, hears your voice, and points to what you're looking for — running silently in the background on Windows 11.

<video src="https://github.com/user-attachments/assets/749430ff-b9d9-4014-8007-09b16f172b8e" autoplay loop muted playsinline width="100%"></video>

![Demo screenshot](screenshot.png)

---

## What is this?

This is a **Windows 11 port** of [Clicky](https://github.com/farzaa/clicky) — an open-source macOS AI companion by [Farzaa](https://github.com/farzaa).

Hold a hotkey, speak a question or command. Clicky captures your screen, transcribes your voice, sends everything to Claude, and:
- **Speaks back** a conversational answer via ElevenLabs TTS
- **Points to the exact element** on screen you asked about — a blue dot flies there with a smooth easing animation, using Claude's Computer Use API for pixel-accurate targeting

No window. No taskbar icon interaction. No UI to manage. Just a floating blue dot and a voice.

---

## Credits & Inspiration

This project would not exist without **[Farzaa's original macOS Clicky](https://github.com/farzaa/clicky)**. The concept, the UX design, the pipeline architecture, and the spirit of the app are entirely his. This repo is a faithful Windows reimplementation, built from scratch in .NET 8 + WPF because macOS-exclusive APIs (ScreenCaptureKit, AVAudioEngine, NSPanel) cannot be ported — they had to be replaced with Windows equivalents.

If you haven't seen the original, go check it out first.

---

## Features

- **Push-to-talk** via a configurable global hotkey (default: `Ctrl+Shift+Space`)
- **Audio waveform animation** on the overlay while you speak — 5 reactive bars driven by live mic level
- **Processing spinner** while Clicky thinks, with a pink pulse the moment your speech is confirmed
- **Animated blue dot** that trails your cursor with spring physics (offset, not stuck to tip)
- **Precision pointing** using Claude's Computer Use API — the dot flies to the detected element with a smooth bezier arc
- **Utterance labels** ("right here!", "found it!") with fade-in/fade-out on arrival
- **ElevenLabs TTS** playback — the dot springs back to cursor-following mode after the response ends
- **Conversation history** — Claude remembers the last 10 turns for context
- **Silent failure feedback** — Turkish messages appear when transcription or API calls fail, so you always know what happened
- **Multi-monitor aware** — captures all screens, sends the one your cursor is on first
- **Single `.exe`** — no installer, no runtime to install for end users

---

## Prerequisites

| Requirement | Notes |
|---|---|
| Windows 11 (or Windows 10 22H2+) | Required for DPI awareness and audio APIs |
| [Anthropic API key](https://console.anthropic.com/) | Claude Sonnet 4.6 — used for both conversation and Computer Use |
| [ElevenLabs API key](https://elevenlabs.io/) | Any voice ID from your account |
| [AssemblyAI API key](https://www.assemblyai.com/) | Streaming speech-to-text (Universal-3 Real-Time Pro model) |
| Microphone | Any Windows-recognized audio input device |

**To run the pre-built binary:** no .NET SDK needed — see [Releases](https://github.com/emreyilmaz46/clicky_windows/releases).

**To build from source:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

---

## Setup

### 1 — Get the binary or clone the repo

**Option A — Pre-built (recommended for users):**
Download `ClickyWindows.exe` from [Releases](https://github.com/emreyilmaz46/clicky_windows/releases) and place it anywhere.

**Option B — Build from source:**
```
git clone https://github.com/emreyilmaz46/clicky_windows.git
cd clicky_windows
dotnet build
```

### 2 — Create your settings file

Copy [`settings.example.json`](settings.example.json) to:
```
%APPDATA%\ClickyWindows\settings.json
```

Fill in your API keys. The folder is created automatically on first run if it doesn't exist.

### 3 — Run

```
dotnet run
# or just double-click ClickyWindows.exe
```

A tray icon appears. Hold `Ctrl+Shift+Space`, speak, release. That's it.

---

## Settings Reference

| Field | Required | Description |
|---|---|---|
| `AnthropicApiKey` | ✅ | Your Anthropic API key |
| `ElevenLabsApiKey` | ✅ | Your ElevenLabs API key |
| `ElevenLabsVoiceId` | ✅ | Voice ID from your ElevenLabs account |
| `AssemblyAiApiKey` | ✅ | Your AssemblyAI API key |
| `ClaudeModel` | — | Defaults to `claude-sonnet-4-6` |
| `HotkeyModifiers` | — | Win32 modifier flags (default: Ctrl+Shift = `0x0006`) |
| `HotkeyVirtualKey` | — | Win32 virtual key code (default: Space = `0x20`) |
| `ClaudeProxyUrl` | — | Optional proxy instead of calling Anthropic directly |
| `ElevenLabsProxyUrl` | — | Optional proxy instead of calling ElevenLabs directly |

---

## How it works

```
Hold hotkey
    │
    ▼
WASAPI microphone capture (NAudio)
    │  PCM16 16kHz mono chunks
    ▼
AssemblyAI Streaming v3 WebSocket
    │  Real-time transcription (u3-rt-pro model)
    │  Final transcript on end_of_turn
    ▼
Release hotkey  →  GDI screen capture (all monitors, cursor-screen first)
    │
    ▼
Claude API (Sonnet 4.6) — SSE streaming
    │  Receives screenshot(s) + transcript + conversation history
    │  Responds conversationally; may include [POINT:x,y:label] tag
    ▼
If POINT detected:
    │  Re-capture screen at Computer Use resolution
    │  Claude Computer Use API — tool_use response with [x, y]
    │  CoordinateHelper scales CU coords → physical pixels → WPF DIPs
    │  Blue dot flies to element (bezier arc, 0.5–1.4s, distance-based)
    │  Utterance label fades in, holds 3s, fades out
    │  Dot springs back to cursor-following
    ▼
ElevenLabs TTS — MP3 synthesis + NAudio WasapiOut playback
    │  Spinner runs through the full HTTP fetch
    │  State → Speaking only when audio literally starts
    ▼
Idle — blue dot resumes trailing cursor with spring physics
```

---

## Architecture

```
ClickyWindows/
├── App.xaml / App.xaml.cs          Entry point, tray icon, single-instance guard
├── MainWindow.xaml.cs              Hidden message-pump window, wires all events
├── OverlayWindow.xaml              Transparent click-through WPF overlay (XAML)
├── OverlayWindow.xaml.cs           Spring physics, flight animation, waveform, spinner
├── Services/
│   ├── CompanionManager.cs         Orchestrates the full voice interaction loop
│   ├── AudioCaptureService.cs      WASAPI capture → PCM16 16kHz mono resampling
│   ├── ScreenCaptureService.cs     GDI multi-monitor JPEG capture + CU resize
│   ├── AssemblyAIService.cs        WebSocket streaming transcription (v3 protocol)
│   ├── ClaudeService.cs            SSE streaming + Computer Use coordinate detection
│   └── ElevenLabsService.cs        TTS synthesis + NAudio MP3 playback
├── Helpers/
│   ├── Win32.cs                    All P/Invoke declarations
│   ├── CoordinateHelper.cs         DPI scaling, CU→WPF coordinate math
│   └── Logger.cs                   File logger (%APPDATA%\ClickyWindows\clicky.log)
├── Models/
│   ├── AppState.cs                 State machine (Idle/Listening/Processing/Speaking)
│   └── ConversationHistory.cs      Last 10 turns for Claude context
└── Settings/
    └── AppSettings.cs              JSON settings loader/saver
```

---

## macOS → Windows API mapping

| Capability | macOS (original Clicky) | This project (Windows) |
|---|---|---|
| Microphone capture | AVAudioEngine | NAudio `WasapiCapture` |
| Screen capture | ScreenCaptureKit | GDI `CopyFromScreen` |
| Global hotkey | CGEvent tap | `RegisterHotKey` Win32 P/Invoke |
| Transparent overlay | NSPanel (.screenSaver level) | WPF + `WS_EX_LAYERED \| WS_EX_TRANSPARENT \| HWND_TOPMOST` |
| TTS playback | AVAudioPlayer | NAudio `Mp3FileReader + WasapiOut` |
| AI coordination | Same | Same (Claude API, AssemblyAI, ElevenLabs) |

---

## Built with

- [.NET 8](https://dotnet.microsoft.com/) + [WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/) — UI framework and overlay window
- [NAudio](https://github.com/naudio/NAudio) — WASAPI audio capture and MP3 playback
- [Claude API](https://docs.anthropic.com/) (Anthropic) — Conversation + Computer Use coordinate detection
- [AssemblyAI](https://www.assemblyai.com/) — Real-time streaming speech-to-text
- [ElevenLabs](https://elevenlabs.io/) — Text-to-speech synthesis

---

## License

MIT — see [LICENSE](LICENSE)

Original macOS Clicky © [Farzaa](https://github.com/farzaa), also MIT licensed.
