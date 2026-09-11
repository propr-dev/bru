# Security Policy

## Reporting a vulnerability

**Do not open a public issue for a security problem.**

Report it privately through
[GitHub Security Advisories](https://github.com/propr-dev/bru/security/advisories/new).
That creates a private thread visible only to the maintainers.

You can expect an acknowledgement within **7 days**. This is a small project maintained
in spare time, so please be patient beyond that — but if you hear nothing in two weeks,
feel free to chase.

If you believe the issue also affects the upstream projects
([clicky_windows](https://github.com/emreyilmaz46/clicky_windows) or
[Clicky](https://github.com/farzaa/clicky)), please report it to them as well.

## Supported versions

| Version | Supported |
|---|---|
| 1.0.x | ✅ |
| < 1.0 | ❌ |

Bru is distributed as a self-contained executable, so there is no auto-update. Security
fixes ship as a new release that you download and replace manually.

---

## What Bru actually does with your data

This matters more than usual here, because Bru sees your screen and hears your voice.
Understand this before you run it.

### It captures your screen

When you hold the hotkey, Bru takes a screenshot of your display and sends it to the
Anthropic API. **Whatever is on screen goes with it** — open documents, password
managers, private messages, client data, unsaved work.

Bru does not filter, blur, or redact anything. If a credential is visible on screen when
you press the hotkey, that credential is transmitted.

### It records your microphone

Audio captured while the hotkey is held is streamed to **AssemblyAI** for transcription.

### It may send text to a third voice provider

If `TtsEngine` is set to `elevenlabs`, the answer text is sent to **ElevenLabs** to be
spoken. Setting `TtsEngine` to `windows` uses the offline Windows SAPI voice instead and
sends nothing.

### Third parties involved

| Service | Receives | When |
|---|---|---|
| [Anthropic](https://www.anthropic.com/legal/privacy) | Screenshots, your transcribed speech, conversation history | Every request |
| [AssemblyAI](https://www.assemblyai.com/legal/privacy-policy) | Microphone audio | Every request |
| [ElevenLabs](https://elevenlabs.io/privacy) | Answer text | Only when `TtsEngine` is `elevenlabs` |

Their handling of that data is governed by their own policies, not this project's.

### It can read and copy your files

When `FileAccessEnabled` is true, Bru can list, search, and copy files — but only within
the directories listed in `AllowedFolders`, and only after you approve each action in the
Bru window.

By design it **cannot move or delete files**. Keep `AllowedFolders` as narrow as you can;
pointing it at your whole user profile defeats the purpose.

To disable file access entirely, set `FileAccessEnabled` to `false`.

---

## How your API keys are stored

Keys live in plain text at:

```
%APPDATA%\ClickyWindows\settings.json
```

**They are not encrypted.** They are protected only by Windows file permissions on your
user profile. Anything running as your user can read them.

What follows from that:

- **Never commit `settings.json`.** It is in `.gitignore`, but check before you push.
- **Never paste it into an issue.** Redact keys from any log or config you share.
- Use a **dedicated, scoped, spend-limited API key** for Bru rather than your main one.
- Rotate the key if you suspect exposure — Anthropic and AssemblyAI both support this.
- On a shared or managed machine, treat Bru's key as already compromised.

The `ClaudeProxyUrl`, `ElevenLabsProxyUrl`, and `AssemblyAiTokenUrl` settings let you
route calls through your own proxy so the keys never sit on the client machine at all.
That is the right approach for any multi-user deployment.

## Logging

Bru writes to `%APPDATA%\ClickyWindows\clicky.log`. That log can contain **what you said
and what was on screen**. Read it before sharing it, and redact accordingly.

## Scope

In scope for a report:

- Leakage of API keys beyond the local user
- Escaping the `AllowedFolders` restriction (path traversal, symlink tricks)
- Bypassing the per-action approval prompt
- Transmitting screen or audio data to a destination other than the documented services
- Remote code execution, privilege escalation

Not in scope:

- The fact that screenshots are sent to Anthropic — that is the documented purpose
- Keys being readable by processes running as your own user — that is inherent to storing
  them unencrypted, and is documented above
- Vulnerabilities in Windows, .NET, or the third-party APIs themselves
