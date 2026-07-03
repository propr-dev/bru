using System.Runtime.InteropServices;
using ClickyWindows.Helpers;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

/// <summary>
/// Speaks text using the Windows built-in voice (SAPI) — free, offline, no API key.
/// Talks to the "SAPI.SpVoice" COM object via late binding (dynamic), so no extra
/// NuGet package (like System.Speech) is required. This replaces ElevenLabs as the
/// default voice to keep the app free to run. ElevenLabsService is kept in the repo
/// in case you want to switch back to premium voices later.
/// </summary>
public class WindowsTtsService : ITtsService
{
    // SAPI Speak() flags
    private const int SVSFlagsAsync = 1;        // return immediately, speak in background
    private const int SVSFPurgeBeforeSpeak = 2; // flush anything currently queued/speaking

    private readonly AppSettings _settings;
    private dynamic? _voice;   // SAPI.SpVoice COM object
    private bool _speaking;

    /// <summary>
    /// Fires immediately before speech begins, so CompanionManager can switch to the
    /// Speaking state (keeps the same contract the ElevenLabs service had).
    /// </summary>
    public event Action? PlaybackStarting;

    public bool IsPlaying => _speaking;

    public WindowsTtsService(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>Speaks the text and returns when playback finishes (or is cancelled).</summary>
    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        PlaybackStarting?.Invoke();
        _speaking = true;
        try
        {
            await Task.Run(() =>
            {
                EnsureVoice();
                _voice!.Speak(text, SVSFlagsAsync);

                // Poll so we can react to cancellation (user pressing the hotkey again).
                while (true)
                {
                    if (ct.IsCancellationRequested)
                    {
                        _voice!.Speak(string.Empty, SVSFPurgeBeforeSpeak); // cut it off
                        ct.ThrowIfCancellationRequested();
                    }

                    // WaitUntilDone returns true once the queued speech has finished.
                    bool done = _voice!.WaitUntilDone(150);
                    if (done) break;
                }
            }, ct);
        }
        finally
        {
            _speaking = false;
        }
    }

    public void StopPlayback()
    {
        try { _voice?.Speak(string.Empty, SVSFPurgeBeforeSpeak); }
        catch { /* nothing playing */ }
        _speaking = false;
    }

    private void EnsureVoice()
    {
        if (_voice != null) return;

        var type = Type.GetTypeFromProgID("SAPI.SpVoice")
            ?? throw new Exception("Windows SAPI voice (SAPI.SpVoice) is not available on this system.");
        _voice = Activator.CreateInstance(type)!;

        // Pick a specific installed voice by name, and log what's available so the
        // user can see their options in clicky.log.
        try
        {
            dynamic voices = _voice.GetVoices();
            int count = voices.Count;
            var names = new List<string>();
            dynamic? chosen = null;
            for (int i = 0; i < count; i++)
            {
                dynamic token = voices.Item(i);
                string desc = Convert.ToString(token.GetDescription()) ?? "";
                names.Add(desc);
                if (chosen == null && !string.IsNullOrWhiteSpace(_settings.WindowsTtsVoice) &&
                    desc.IndexOf(_settings.WindowsTtsVoice, StringComparison.OrdinalIgnoreCase) >= 0)
                    chosen = token;
            }
            Logger.Log($"[TTS] Installed voices: {string.Join(" | ", names)}");
            if (chosen != null)
            {
                _voice.Voice = chosen;
                Logger.Log($"[TTS] Selected voice matching '{_settings.WindowsTtsVoice}'");
            }
        }
        catch (Exception ex) { Logger.Log($"[TTS] Voice listing/selection skipped: {ex.Message}"); }

        // Optional voice tuning. Rate: -10..10, Volume: 0..100.
        try
        {
            _voice.Rate = _settings.WindowsTtsRate;
            _voice.Volume = _settings.WindowsTtsVolume;
        }
        catch { /* leave defaults if the COM object rejects these */ }

        Logger.Log("[TTS] Windows SAPI voice initialized");
    }

    public void Dispose()
    {
        try
        {
            if (_voice != null && Marshal.IsComObject(_voice))
                Marshal.FinalReleaseComObject(_voice);
        }
        catch { /* ignore */ }
        _voice = null;
    }
}
