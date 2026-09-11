## What does this change?

<!-- One or two sentences. What is different after this PR? -->

## Why?

<!-- Link an issue with "Closes #123" if there is one. -->

## Which part of Bru does it touch?

<!-- Mark with an x. -->

- [ ] Pointing (`ClaudeService`, `CoordinateHelper`, `OverlayWindow`)
- [ ] Voice in (`AudioCaptureService`, `AssemblyAIService`)
- [ ] Voice out (`ITtsService`, `WindowsTtsService`, `ElevenLabsService`)
- [ ] Screen vision (`ScreenCaptureService`)
- [ ] File assistant (`FileService`)
- [ ] UI (`StatusWindow`, `MainWindow`, `OverlayWindow`)
- [ ] Orchestration (`CompanionManager`)
- [ ] Settings / configuration
- [ ] Build, CI, or documentation

## How did you test it?

<!--
There is no automated test suite for the interaction loop — it needs a microphone,
a screen, and a real API key. So please say what you actually exercised by hand.
-->

- [ ] `dotnet build -c Release` succeeds with no new warnings
- [ ] Ran the app and exercised the affected path manually
- [ ] Tested on more than one monitor / DPI scale (required for pointing changes)

## Checklist

- [ ] No API keys, tokens, or personal paths in the diff
- [ ] `settings.example.json` updated if a new setting was added
- [ ] `CHANGELOG.md` updated under "Unreleased"
- [ ] Existing code style followed (`_camelCase` private fields, file-scoped namespaces)

## Notes for the reviewer

<!-- Anything you are unsure about, or deliberately left out. -->
