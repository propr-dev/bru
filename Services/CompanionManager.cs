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
    // Stores the final transcript as soon as AssemblyAI delivers it (may arrive before key release)
    private string? _pendingTranscript;

    public event Action<AppState>? StateChanged;
    public event Action<double, double, string>? PointReceived;
    public event Action<float>? AudioLevelChanged;

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
        _pendingTranscript = null;

        _audio.Start();
        Logger.Log("[Audio] WASAPI capture started");

        _assemblyAI = new AssemblyAIService(_settings.AssemblyAiApiKey);
        _assemblyAI.InterimTranscriptReceived += text => Logger.Log($"[ASR] Interim: {text}");

        // Subscribe to final transcript NOW (before key release) so we never miss it.
        // AssemblyAI may fire end_of_turn=true while the key is still held.
        _assemblyAI.FinalTranscriptReceived += text =>
        {
            Logger.Log($"[ASR] Final transcript (captured): \"{text}\"");
            _pendingTranscript = text;
        };

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

        // Get final transcript.
        // It may have already arrived while the key was still held (end_of_turn auto-detected).
        string transcript = "";
        if (_pendingTranscript != null)
        {
            // Already have it — use immediately, no need to wait
            transcript = _pendingTranscript;
            Logger.Log($"[ASR] Using pre-captured transcript: \"{transcript}\"");
        }
        else if (_assemblyAI != null)
        {
            // Not yet received — wait for it (with a TCS that the already-subscribed handler fills)
            var tcs = new TaskCompletionSource<string>();
            _assemblyAI.FinalTranscriptReceived += text => tcs.TrySetResult(text);

            try
            {
                Logger.Log("[ASR] No transcript yet — sending force_end_utterance, waiting...");
                await _assemblyAI.FinalizeAsync(_sessionCts!.Token);
                transcript = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8), _sessionCts!.Token);
            }
            catch (TimeoutException)
            {
                Logger.Error("No transcript received after 8s — check your microphone and AssemblyAI key");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error($"Transcription error: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(transcript))
        {
            Logger.Info($"Heard: \"{transcript}\"");
            await ProcessResponseAsync(transcript, screenshots);
        }
        else
        {
            Logger.Log("[Pipeline] No transcript — returning to Idle");
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
            State = AppState.Speaking;
            try
            {
                await _tts.SpeakAsync(fullText, _sessionCts!.Token);
                Logger.Log("[TTS] Playback complete");
            }
            catch (Exception ex)
            {
                Logger.Error($"TTS failed: {ex.Message}");
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
