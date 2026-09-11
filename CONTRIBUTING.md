# Contributing to Bru

Thanks for your interest. This document covers what you need to build Bru, how the code
is organised, and what tends to get a change merged.

Bru is a fork of [clicky_windows](https://github.com/emreyilmaz46/clicky_windows), itself
a Windows port of [Clicky](https://github.com/farzaa/clicky). If your change is really
about the shared core rather than Bru specifically, consider sending it upstream too.

---

## Prerequisites

| Requirement | Why |
|---|---|
| **Windows 10 build 22000+ / Windows 11** | The target framework is `net8.0-windows10.0.22000.0` |
| **.NET 8 SDK** | [Download](https://dotnet.microsoft.com/download/dotnet/8.0) |
| **x64 machine** | The project targets x64 only |
| **A microphone** | Every interaction starts with push-to-talk |
| **An Anthropic API key** | [console.anthropic.com](https://console.anthropic.com/) |
| **An AssemblyAI API key** | [assemblyai.com](https://www.assemblyai.com/) |

Visual Studio 2022, JetBrains Rider, and VS Code with the C# Dev Kit all work. There is no
`.sln` — open the folder or the `.csproj` directly.

> Running Bru costs money. Every interaction is one Anthropic call plus streaming
> transcription. Use a **spend-limited key** while developing, and prefer a cheap
> `ClaudeModel` — pointing accuracy depends on `PointingModel`, chat quality does not.

## Getting set up

```bash
git clone https://github.com/propr-dev/bru.git
cd bru
dotnet restore
dotnet build -c Release
```

Then create your settings file:

```bash
mkdir -p "$APPDATA/ClickyWindows"
cp settings.example.json "$APPDATA/ClickyWindows/settings.json"
```

Fill in `AnthropicApiKey` and `AssemblyAiApiKey`. Everything else has a working default.

Run it:

```bash
dotnet run
```

Logs go to `%APPDATA%\ClickyWindows\clicky.log`. Installed Windows voices are listed there
at startup, which is how you find a name for `WindowsTtsVoice`.

To produce the shipping single-file executable:

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

That is roughly 180 MB — self-contained means the .NET runtime is bundled, so users
install nothing.

---

## How the code is laid out

```
Services/
  CompanionManager      orchestrates hotkey → audio → ASR → Claude → dot + voice
  HotkeyService         global push-to-talk hotkey registration
  AudioCaptureService   WASAPI mic capture → PCM16 16 kHz mono
  AssemblyAIService     streaming speech-to-text (v3 WebSocket)
  ScreenCaptureService  screenshot capture
  ClaudeService         conversation, file tool-use loop, two-pass pointing
  FileService           folder-scoped list/copy with staged approval
  ITtsService           voice-out abstraction
    WindowsTtsService     free, offline, SAPI
    ElevenLabsService     paid, networked
Models/         AppState, ConversationHistory, ScreenAnnotation
Helpers/        CoordinateHelper (screen ↔ image maths), Logger, Win32 interop
Settings/       AppSettings — load/save of settings.json
OverlayWindow   fullscreen click-through overlay: the dot, waveform, spinner
StatusWindow    the Bru panel: living core, latest exchange, permission prompts
MainWindow      tray host
```

**`CompanionManager` is the spine.** If you are trying to understand how a request flows
end to end, start there and follow it outward.

**Pointing is two-pass.** A coarse locate pass narrows the region, then a native-resolution
zoom pass refines the coordinate. `CoordinateHelper` does the mapping between screen space
and image space. This is the most fragile part of the codebase and the most sensitive to
multi-monitor and DPI differences — changes here need testing on more than one setup.

## Code style

The codebase is internally consistent. Match it:

- **File-scoped namespaces** — `namespace ClickyWindows.Services;`
- **`_camelCase`** for private fields, **PascalCase** for public members and
  `static readonly`
- **4 spaces**, no tabs
- **XML doc comments** on public types and anything non-obvious
- **Nullable is enabled** — do not silence warnings with `!` unless you can justify it
- No `this.` qualification

An `.editorconfig` encodes all of this, so most editors will do the right thing.

**The build is warning-free and CI enforces that with `-warnaserror`.** A PR that
introduces warnings will fail.

## Testing

There is no automated test suite for the interaction loop, and that is a deliberate
limitation rather than an oversight — it needs a microphone, a real screen, and paid API
calls. So testing is manual, and your PR should say what you actually exercised.

At minimum:

```bash
dotnet build -c Release -warnaserror
```

Then run the app and exercise the path you changed. For **any pointing change**, test on
at least two of:

- A single monitor at 100% scaling
- A multi-monitor setup
- A display at 125% / 150% / 175% scaling
- A 4K display

Pointing bugs almost always come from coordinate maths that was only ever tried on one
configuration.

## Pull requests

1. Branch from `main`
2. Keep the change focused — one concern per PR
3. Update `settings.example.json` if you add a setting
4. Add an entry to `CHANGELOG.md` under `## [Unreleased]`
5. Fill in the PR template, especially the manual-testing section

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/):

```
feat: add Afrikaans voice selection
fix: correct dot placement on displays above 150% scaling
docs: document the proxy settings
refactor: extract coordinate mapping from ClaudeService
ci: build the single-file executable on every PR
```

## What is likely to be accepted

Welcome:

- Pointing accuracy fixes, especially multi-monitor and high-DPI
- Local-language support — this is on the roadmap and genuinely wanted
- Latency reductions anywhere in the loop
- Accessibility improvements
- Documentation that makes first-run less confusing

Harder sell:

- Anything that widens what Bru can do to your machine. The narrow surface — read the
  screen, listen, answer, point, copy files inside approved folders, **never move or
  delete** — is a deliberate safety property, not a missing feature.
- New paid third-party dependencies
- Large refactors without a problem they solve

If you are planning something substantial, open an issue first so you do not spend a
weekend on something that will not land.

## Security

Do not open a public issue for a vulnerability. See [SECURITY.md](./SECURITY.md).

Never commit `settings.json`, an API key, or an unredacted `clicky.log` — that log can
contain what you said and what was on your screen.

## Licence

Contributions are accepted under the [MIT Licence](./LICENSE), which covers both the
original work and this fork's additions.
