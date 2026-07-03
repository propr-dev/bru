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
    private readonly ITtsService _tts;
    private readonly ConversationHistory _history;
    private readonly FileService _files;

    /// <summary>The shared file layer — the Bru window subscribes to its permission events.</summary>
    public FileService Files => _files;

    private AssemblyAIService? _assemblyAI;
    private CancellationTokenSource? _sessionCts;
    // Single TCS created at key-press time, resolved by FinalTranscriptReceived.
    // Using one TCS for the full lifetime of a session eliminates the race condition
    // where end_of_turn fires between our null-check and TCS subscription.
    private TaskCompletionSource<string>? _transcriptTcs;

    public event Action<AppState>? StateChanged;
    public event Action<double, double, string>? PointReceived;
    /// <summary>Temporary on-screen drawings (boxes/arrows/lines/notes), physical pixel coords.</summary>
    public event Action<List<ScreenAnnotation>>? AnnotationsReceived;
    public event Action<float>? AudioLevelChanged;
    /// <summary>Fires with a short Turkish message when a pipeline stage fails silently.</summary>
    public event Action<string>? FeedbackReceived;
    /// <summary>Fires the instant a final transcript arrives — triggers the spinner pulse.</summary>
    public event Action? TranscriptConfirmed;
    /// <summary>What the user said (for the Bru window's activity view).</summary>
    public event Action<string>? UserTranscript;
    /// <summary>What Bru replied (for the Bru window's activity view).</summary>
    public event Action<string>? AssistantReply;

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
        _files = new FileService(settings.AllowedFolders, settings.FileAccessEnabled);
        _claude = new ClaudeService(settings, _history, _files);
        _tts = string.Equals(settings.TtsEngine, "elevenlabs", StringComparison.OrdinalIgnoreCase)
            ? new ElevenLabsService(settings)
            : new WindowsTtsService(settings);
        Logger.Log($"[TTS] Engine: {settings.TtsEngine}");

        _audio.PowerLevelChanged += level => AudioLevelChanged?.Invoke(level);

        // Transition to Speaking when audio literally starts. Fires once per streamed
        // sentence, so guard against re-announcing the state on every chunk.
        _tts.PlaybackStarting += () => { if (State != AppState.Speaking) State = AppState.Speaking; };
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

        // Attach BEFORE connecting: chunks captured while the socket is still being
        // established are buffered inside AssemblyAIService and flushed on connect,
        // so the first words of the sentence are no longer lost.
        _audio.AudioChunkAvailable += OnAudioChunk;

        try
        {
            Logger.Log("[ASR] Connecting to AssemblyAI...");
            await _assemblyAI.ConnectAsync(_sessionCts.Token);
            Logger.Log("[ASR] Connected OK — streaming audio");
        }
        catch (Exception ex)
        {
            Logger.Error($"AssemblyAI connection failed: {ex.Message}");
            _audio.AudioChunkAvailable -= OnAudioChunk;
            _audio.Stop();
            State = AppState.Idle;
            FeedbackReceived?.Invoke("couldn't connect");
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
                    FeedbackReceived?.Invoke("didn't quite catch that :(");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Logger.Error($"[ASR] Retry error: {ex.Message}");
                    FeedbackReceived?.Invoke("didn't quite catch that :(");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error($"[ASR] Transcription error: {ex.Message}");
                FeedbackReceived?.Invoke("didn't quite catch that :(");
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
                FeedbackReceived?.Invoke("didn't quite catch that :(");
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
        UserTranscript?.Invoke(transcript);

        string responseText;
        PointTarget? detectedPoint = null;

        // Identity of THIS turn's session. If the user interrupts (new hotkey press),
        // a new CTS replaces _sessionCts — this stale turn must not touch State or
        // show error feedback afterwards, or it stomps the new session's Listening.
        var session = _sessionCts;

        // Sentence queue: Bru starts SPEAKING on the first streamed sentence,
        // while the rest of the response (and its drawing tags) is still arriving.
        var sentences = System.Threading.Channels.Channel.CreateUnbounded<string>();
        bool ttsFailed = false;
        var speakTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var s in sentences.Reader.ReadAllAsync(_sessionCts!.Token))
                {
                    try
                    {
                        await _tts.SpeakAsync(s, _sessionCts.Token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        if (!ttsFailed)
                        {
                            ttsFailed = true;
                            Logger.Error($"TTS failed: {ex.Message}");
                            var preview = s.Length > 30 ? s[..30].TrimEnd() + "…" : s;
                            FeedbackReceived?.Invoke(preview);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* user interrupted */ }
        });

        try
        {
            responseText = await _claude.GetResponseAsync(
                transcript, screenshots,
                sentence => sentences.Writer.TryWrite(sentence),
                _sessionCts!.Token);
        }
        catch (Exception ex)
        {
            sentences.Writer.TryComplete();
            if (session?.IsCancellationRequested == true)
            {
                // User interrupted — normal flow, no error theatre.
                Logger.Log("[Claude] Turn cancelled by user");
                return;
            }
            Logger.Error($"Claude API error: {ex.Message}");
            FeedbackReceived?.Invoke("sorry, I couldn't respond");
            if (ReferenceEquals(session, _sessionCts))
                State = AppState.Idle;
            return;
        }

        var points = ClaudeService.ParsePoints(responseText);
        if (points.Count > 0)
            detectedPoint = points[0];

        // ── Screen drawings: scale from image space → physical and hand to the overlay ──
        var annotations = ClaudeService.ParseAnnotations(responseText);
        if (annotations.Count > 0 && screenshots.Count > 0)
        {
            var shot = screenshots[0];
            var sb = shot.Bounds;
            double ax = (double)sb.Width  / Math.Max(1, shot.ImageWidth);
            double ay = (double)sb.Height / Math.Max(1, shot.ImageHeight);

            var scaled = annotations.Select(a => a.Kind switch
            {
                // Box: A=(x,y) scales as a point, B=(w,h) scales as a size
                AnnotationKind.Box => a with
                {
                    Ax = sb.X + a.Ax * ax, Ay = sb.Y + a.Ay * ay,
                    Bx = a.Bx * ax,        By = a.By * ay,
                },
                // Arrow/Line: both ends are points
                AnnotationKind.Arrow or AnnotationKind.Line => a with
                {
                    Ax = sb.X + a.Ax * ax, Ay = sb.Y + a.Ay * ay,
                    Bx = sb.X + a.Bx * ax, By = sb.Y + a.By * ay,
                },
                // Note: single anchor point
                _ => a with { Ax = sb.X + a.Ax * ax, Ay = sb.Y + a.Ay * ay },
            }).ToList();

            Logger.Log($"[Draw] {scaled.Count} annotation(s): " +
                string.Join(", ", scaled.Select(s => s.Kind.ToString().ToLower())));
            WpfApp.Current.Dispatcher.Invoke(() => AnnotationsReceived?.Invoke(scaled));
        }

        // ── Pointing (ported from the original macOS Clicky's one-call design) ──
        // The main model's [POINT:x,y] tag is in the labeled screenshot's pixel space
        // (each image label states its exact dimensions), so the tag itself is a valid
        // coarse estimate. One optional native-resolution "zoom" Computer Use pass
        // around that estimate then sharpens the aim for small elements.
        if (detectedPoint != null && screenshots.Count > 0)
        {
            var primaryShot = screenshots[0]; // cursor's screen (sorted first)
            var b = primaryShot.Bounds;

            // Scale tag coords from image space → physical screen space.
            double sx = (double)b.Width  / Math.Max(1, primaryShot.ImageWidth);
            double sy = (double)b.Height / Math.Max(1, primaryShot.ImageHeight);
            int absX = b.X + (int)Math.Round(Math.Clamp(detectedPoint.X, 0, primaryShot.ImageWidth)  * sx);
            int absY = b.Y + (int)Math.Round(Math.Clamp(detectedPoint.Y, 0, primaryShot.ImageHeight) * sy);
            Logger.Log($"[Point] Tag ({detectedPoint.X},{detectedPoint.Y}) in " +
                       $"{primaryShot.ImageWidth}x{primaryShot.ImageHeight} → abs ({absX},{absY}) " +
                       $"label=\"{detectedPoint.Label}\"");

            // Zoom pass: native-res crop around the estimate, one CU call. If it
            // fails, the scaled tag coordinate is already a sane place to land.
            try
            {
                // Crop generously: if the tag estimate is off by a couple hundred px,
                // the real element must still be INSIDE the crop or the zoom pass
                // can't find it. Native-res 760×560 keeps detail high regardless.
                const int rw = 760, rh = 560;
                int rx = Math.Clamp(absX - rw / 2, b.X, Math.Max(b.X, b.Right - rw));
                int ry = Math.Clamp(absY - rh / 2, b.Y, Math.Max(b.Y, b.Bottom - rh));
                var region = new System.Drawing.Rectangle(
                    rx, ry, Math.Min(rw, b.Width), Math.Min(rh, b.Height));

                var crop = _screen.CaptureRegion(region);
                if (crop != null)
                {
                    var (zx, zy) = await _claude.DetectElementAsync(
                        crop.Base64, detectedPoint.Label,
                        region.Width, region.Height, _sessionCts!.Token);
                    if (zx >= 0)
                    {
                        absX = region.X + zx;
                        absY = region.Y + zy;
                        Logger.Log($"[Point] Zoom refinement: region-rel ({zx},{zy}) → abs ({absX},{absY})");
                    }
                    else
                    {
                        Logger.Log("[Point] Zoom pass returned no coordinate — using tag estimate");
                    }
                }
            }
            catch (Exception zex)
            {
                Logger.Log($"[Point] Zoom pass skipped: {zex.Message} — using tag estimate");
            }

            Logger.Log($"[Point] Final: ({absX},{absY}) label=\"{detectedPoint.Label}\"");
            WpfApp.Current.Dispatcher.Invoke(() =>
                PointReceived?.Invoke(absX, absY, detectedPoint.Label));
        }

        var fullText = ClaudeService.StripPointTags(responseText);
        Logger.Log($"[Claude] Response text: \"{fullText}\"");
        if (!string.IsNullOrWhiteSpace(fullText))
            AssistantReply?.Invoke(fullText);

        // All sentences are queued — wait for the speech worker to finish them.
        sentences.Writer.TryComplete();
        try { await speakTask; } catch (OperationCanceledException) { }
        Logger.Log("[TTS] Playback complete");

        // Only unwind to Idle if no newer session has taken over in the meantime.
        if (ReferenceEquals(session, _sessionCts))
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
