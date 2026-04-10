using ClickyWindows.Helpers;
using ClickyWindows.Models;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

/// <summary>
/// Orchestrates the full voice interaction loop:
/// hotkey press → audio capture → transcription → Claude → overlay + TTS
/// </summary>
public class CompanionManager : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly AudioCaptureService _audio;
    private readonly ScreenCaptureService _screen;
    private readonly ClaudeService _claude;
    private readonly ElevenLabsService _tts;
    private readonly ConversationHistory _history;

    private AssemblyAIService? _assemblyAI;
    private CancellationTokenSource? _sessionCts;
    // Single TCS created at key-press time, resolved by FinalTranscriptReceived.
    // Using one TCS for the full lifetime of a session eliminates the race condition
    // where end_of_turn fires between our null-check and TCS subscription.
    private TaskCompletionSource<string>? _transcriptTcs;

    public event Action<AppState>? StateChanged;
    public event Action<double, double, string>? PointReceived;
    public event Action<float>? AudioLevelChanged;
    /// <summary>Fires with a short Turkish message when a pipeline stage fails silently.</summary>
    public event Action<string>? FeedbackReceived;
    /// <summary>Fires the instant a final transcript arrives — triggers the spinner pulse.</summary>
    public event Action? TranscriptConfirmed;

    private AppState _state = AppState.Idle;
    private AppState State
    {
        get => _state;
        set
        {
            _state = value;
            Logger.Log($"[State] → {value}");
            StateChanged?.Invoke(value);
        }
    }

    public CompanionManager(AppSettings settings)
    {
        _settings = settings;
        _history = new ConversationHistory(maxTurns: 10);
        _audio = new AudioCaptureService();
        _screen = new ScreenCaptureService();
        _claude = new ClaudeService(settings, _history);
        _tts = new ElevenLabsService(settings);

        _audio.PowerLevelChanged += level => AudioLevelChanged?.Invoke(level);

        // Fix 1: transition to Speaking only when audio literally starts, not before the HTTP fetch
        _tts.PlaybackStarting += () => State = AppState.Speaking;
    }

    // ── Push-to-talk lifecycle ──────────────────────────────────────────────

    public async Task OnPushToTalkPressed()
    {
        // Allow pressing hotkey while TTS is speaking — interrupt playback and listen again
        if (State == AppState.Speaking)
        {
            Logger.Log("[Hotkey] Interrupting TTS — stopping playback");
            _tts.StopPlayback();
            _sessionCts?.Cancel();
            _state = AppState.Idle; // set directly to avoid double-firing state events
            await Task.Delay(80);   // brief pause for audio device to release
        }

        if (State != AppState.Idle)
        {
            Logger.Log($"[Hotkey] Pressed but state={State} — ignoring");
            return;
        }

        Logger.Log("[Hotkey] Push-to-talk PRESSED");
        State = AppState.Listening;
        _sessionCts = new CancellationTokenSource();

        // Create the TCS *before* audio starts so FinalTranscriptReceived can never
        // fire between our check and our subscription — the race condition is gone.
        // RunContinuationsAsynchronously prevents deadlocks if TrySetResult is called
        // from the WebSocket receive-loop thread while we're awaiting on the UI thread.
        _transcriptTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        _audio.Start();
        Logger.Log("[Audio] WASAPI capture started");

        _assemblyAI = new AssemblyAIService(_settings.AssemblyAiApiKey);
        _assemblyAI.InterimTranscriptReceived += text => Logger.Log($"[ASR] Interim: {text}");
        _assemblyAI.FinalTranscriptReceived += text =>
        {
            Logger.Log($"[ASR] Final transcript: \"{text}\"");
            // Resolve for any end_of_turn, including empty (silence). Empty string is
            // handled as "nothing heard" at the await site — no 5-second wait needed.
            _transcriptTcs?.TrySetResult(text);
        };
        // When the socket closes (cleanly or via 3006 error) without a final Turn,
        // unblock the TCS immediately so we don't wait 5+ seconds for a timeout.
        _assemblyAI.ReceiveLoopEnded += () => _transcriptTcs?.TrySetResult("");

        try
        {
            Logger.Log("[ASR] Connecting to AssemblyAI...");
            await _assemblyAI.ConnectAsync(_sessionCts.Token);
            Logger.Log("[ASR] Connected OK — streaming audio");
            _audio.AudioChunkAvailable += OnAudioChunk;
        }
        catch (Exception ex)
        {
            Logger.Error($"AssemblyAI connection failed: {ex.Message}");
            _audio.Stop();
            State = AppState.Idle;
            FeedbackReceived?.Invoke("bağlanamadım");
        }
    }

    public async Task OnPushToTalkReleased()
    {
        Logger.Log($"[Hotkey] Push-to-talk RELEASED (state={State})");

        if (State != AppState.Listening)
        {
            Logger.Log("[Hotkey] State is not Listening — an error occurred earlier, see log");
            return;
        }

        State = AppState.Processing;

        _audio.Stop();
        _audio.AudioChunkAvailable -= OnAudioChunk;
        Logger.Log("[Audio] Capture stopped");

        // Capture screens
        List<ScreenshotResult> screenshots;
        try
        {
            screenshots = _screen.CaptureAll();
            Logger.Log($"[Screen] Captured {screenshots.Count} display(s)");
        }
        catch (Exception ex)
        {
            Logger.Log($"[Screen] Capture failed: {ex.Message}");
            screenshots = [];
        }

        // Unified transcript path — the TCS was subscribed before audio started, so whether
        // end_of_turn fired while the key was held or fires now, TrySetResult already ran or will.
        // No dual code paths, no race window.
        string transcript = "";
        if (_transcriptTcs != null && _assemblyAI != null)
        {
            // Always send force_end_utterance after stopping audio to close the turn promptly.
            // If end_of_turn already fired this is a no-op from AssemblyAI's perspective.
            try
            {
                Logger.Log("[ASR] Sending force_end_utterance...");
                await _assemblyAI.FinalizeAsync(_sessionCts!.Token);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ASR] FinalizeAsync failed (non-fatal): {ex.Message}");
            }

            // First attempt: 5 s
            try
            {
                transcript = await _transcriptTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), _sessionCts!.Token);
                Logger.Info($"Heard: \"{transcript}\"");
                TranscriptConfirmed?.Invoke();
            }
            catch (TimeoutException)
            {
                // Retry once — send another force_end_utterance and wait 3 more seconds.
                // Covers the case where AssemblyAI needed a moment longer to process.
                Logger.Log("[ASR] No transcript after 5s — retrying with second force_end_utterance...");
                try
                {
                    await _assemblyAI.FinalizeAsync(_sessionCts!.Token);
                    transcript = await _transcriptTcs.Task.WaitAsync(TimeSpan.FromSeconds(3), _sessionCts!.Token);
                    Logger.Info($"Heard (retry): \"{transcript}\"");
                    TranscriptConfirmed?.Invoke();
                }
                catch (TimeoutException)
                {
                    Logger.Error("[ASR] No transcript after 8s total — giving up");
                    FeedbackReceived?.Invoke("tam duyamadım seni :(");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Logger.Error($"[ASR] Retry error: {ex.Message}");
                    FeedbackReceived?.Invoke("tam duyamadım seni :(");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error($"[ASR] Transcription error: {ex.Message}");
                FeedbackReceived?.Invoke("tam duyamadım seni :(");
            }
        }

        if (!string.IsNullOrWhiteSpace(transcript))
        {
            await ProcessResponseAsync(transcript, screenshots);
        }
        else
        {
            Logger.Log("[Pipeline] No transcript — returning to Idle");
            // Show feedback if the session ended cleanly but with no usable speech —
            // i.e. not a user cancellation, and feedback not already shown by a timeout catch.
            if (!_sessionCts!.IsCancellationRequested)
                FeedbackReceived?.Invoke("tam duyamadım seni :(");
            State = AppState.Idle;
        }

        if (_assemblyAI != null)
        {
            await _assemblyAI.DisposeAsync();
            _assemblyAI = null;
        }
    }

    private void OnAudioChunk(byte[] pcm16)
    {
        _ = _assemblyAI?.SendAudioAsync(pcm16);
    }

    // ── Claude + TTS ────────────────────────────────────────────────────────

    private async Task ProcessResponseAsync(string transcript, List<ScreenshotResult> screenshots)
    {
        Logger.Log($"[Claude] Sending to Claude: \"{transcript}\" with {screenshots.Count} screenshot(s)");

        var responseBuilder = new System.Text.StringBuilder();
        PointTarget? detectedPoint = null;

        try
        {
            await foreach (var chunk in _claude.StreamResponseAsync(transcript, screenshots, _sessionCts!.Token))
            {
                responseBuilder.Append(chunk);

                // Capture the first POINT tag label for CU refinement — don't fire yet
                if (detectedPoint == null)
                {
                    var pts = ClaudeService.ParsePoints(responseBuilder.ToString());
                    if (pts.Count > 0)
                        detectedPoint = pts[0];
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Claude API error: {ex.Message}");
            FeedbackReceived?.Invoke("üzgünüm yanıt veremedim");
            State = AppState.Idle;
            return;
        }

        // Two-phase point detection: use Computer Use API for precise coordinates
        if (detectedPoint != null && screenshots.Count > 0)
        {
            Logger.Log($"[Claude] POINT detected: label=\"{detectedPoint.Label}\" — starting CU coordinate refinement");
            bool cuSucceeded = false;

            try
            {
                var primaryShot = screenshots[0]; // cursor's screen (sorted first)
                var (cuW, cuH) = CoordinateHelper.DetectComputerUseResolution(
                    primaryShot.Bounds.Width, primaryShot.Bounds.Height);

                var resized = _screen.CaptureResized(primaryShot.Bounds, cuW, cuH);
                if (resized != null)
                {
                    var (physX, physY) = await _claude.DetectElementAsync(
                        resized.Base64, detectedPoint.Label,
                        primaryShot.Bounds.Width, primaryShot.Bounds.Height,
                        _sessionCts!.Token);

                    if (physX >= 0)
                    {
                        Logger.Log($"[CU] Precise POINT: ({physX},{physY}) label=\"{detectedPoint.Label}\"");
                        WpfApp.Current.Dispatcher.Invoke(() =>
                            PointReceived?.Invoke(physX, physY, detectedPoint.Label));
                        cuSucceeded = true;
                    }
                    else
                    {
                        Logger.Log("[CU] No tool_use coordinate in response");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[CU] Computer Use detection failed: {ex.Message}");
            }

            // Fallback: rough [POINT:x,y] coords from text response
            if (!cuSucceeded)
            {
                Logger.Log($"[CU] Falling back to rough coords ({detectedPoint.X},{detectedPoint.Y})");
                WpfApp.Current.Dispatcher.Invoke(() =>
                    PointReceived?.Invoke(detectedPoint.X, detectedPoint.Y, detectedPoint.Label));
            }
        }

        var fullText = ClaudeService.StripPointTags(responseBuilder.ToString());
        Logger.Log($"[Claude] Response text: \"{fullText}\"");

        if (!string.IsNullOrWhiteSpace(fullText))
        {
            // State transitions to Speaking inside ElevenLabsService.PlaybackStarting
            // (right before Play()), so the spinner runs through the full HTTP fetch.
            try
            {
                await _tts.SpeakAsync(fullText, _sessionCts!.Token);
                Logger.Log("[TTS] Playback complete");
            }
            catch (OperationCanceledException)
            {
                // Interrupted by user pressing hotkey again — normal flow
            }
            catch (Exception ex)
            {
                Logger.Error($"TTS failed: {ex.Message}");
                // Claude responded but we can't play it — show the first ~30 chars as text
                var preview = fullText.Length > 30
                    ? fullText[..30].TrimEnd() + "…"
                    : fullText;
                FeedbackReceived?.Invoke(preview);
            }
        }

        State = AppState.Idle;
    }

    public void StopSpeaking() => _tts.StopPlayback();

    public async ValueTask DisposeAsync()
    {
        _sessionCts?.Cancel();
        _audio.Dispose();
        _tts.Dispose();
        if (_assemblyAI != null)
            await _assemblyAI.DisposeAsync();
        _sessionCts?.Dispose();
    }
}
