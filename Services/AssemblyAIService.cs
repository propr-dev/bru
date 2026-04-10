using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClickyWindows.Helpers;

namespace ClickyWindows.Services;

/// <summary>
/// Streams PCM16 audio to AssemblyAI's real-time transcription WebSocket API (v3).
///
/// AssemblyAI Streaming v3 protocol (corrected):
///   URL:     wss://streaming.assemblyai.com/v3/ws
///   Params:  ?sample_rate=16000&encoding=pcm_s16le&speech_model=u3-rt-pro
///   Auth:    Authorization: {api_key}  (request header)
///   Audio:   binary PCM16 frames (little-endian, 50–1000ms chunks)
///
///   Server → Client message types (field: "type"):
///     "Begin"       – session started  { id, expires_at }
///     "Turn"        – transcript       { transcript, end_of_turn, words, ... }
///     "Termination" – session ended    { audio_duration_seconds, session_duration_seconds }
///
///   Client → Server control (JSON text frame):
///     {"force_end_utterance": true}   — finalize current turn immediately
/// </summary>
public class AssemblyAIService : IAsyncDisposable
{
    private readonly string _apiKey;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private int _messagesReceived;

    public event Action<string>? InterimTranscriptReceived;
    public event Action<string>? FinalTranscriptReceived;
    /// <summary>
    /// Fires when the WebSocket receive loop exits for any reason (clean close, error, cancellation).
    /// CompanionManager uses this to unblock the transcript TCS immediately instead of timing out.
    /// </summary>
    public event Action? ReceiveLoopEnded;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public AssemblyAIService(string apiKey)
    {
        _apiKey = apiKey;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", _apiKey);

        // u3-rt-pro = Universal-3 Real-Time Pro (highest accuracy, ~300ms latency)
        var uri = new Uri("wss://streaming.assemblyai.com/v3/ws?sample_rate=16000&encoding=pcm_s16le&speech_model=u3-rt-pro");
        await _ws.ConnectAsync(uri, _cts.Token);

        _ = ReceiveLoopAsync(_cts.Token);
    }

    public async Task SendAudioAsync(byte[] pcm16Chunk, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open) return;
        await _ws.SendAsync(new ArraySegment<byte>(pcm16Chunk), WebSocketMessageType.Binary, true, ct);
    }

    public async Task FinalizeAsync(CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open)
        {
            Logger.Log("[ASR] FinalizeAsync: WebSocket not open");
            return;
        }
        // v3 control message — force end of current turn
        var msg = JsonSerializer.Serialize(new { force_end_utterance = true });
        Logger.Log($"[ASR] Sending: {msg}");
        var bytes = Encoding.UTF8.GetBytes(msg);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[65536];
        var sb = new StringBuilder();

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Logger.Log($"[ASR] Closed by server: {_ws.CloseStatus} {_ws.CloseStatusDescription}");
                    break;
                }

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                var raw = sb.ToString();
                sb.Clear();

                _messagesReceived++;
                Logger.Log($"[ASR] Msg #{_messagesReceived}: {raw}");
                ParseMessage(raw);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Logger.Log($"[ASR] WebSocket error: {ex.Message}");
        }
        finally
        {
            // Always fire so CompanionManager can unblock the TCS immediately
            // rather than waiting for the full 5-second timeout.
            ReceiveLoopEnded?.Invoke();
        }
    }

    private void ParseMessage(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node == null) return;

            var type = node["type"]?.GetValue<string>();

            switch (type)
            {
                case "Begin":
                    Logger.Log($"[ASR] Session begun (id={node["id"]?.GetValue<string>()})");
                    break;

                case "Turn":
                    // v3 uses a single "Turn" message for both interim and final.
                    // end_of_turn=false → interim, end_of_turn=true → final/complete turn
                    var transcript = node["transcript"]?.GetValue<string>() ?? "";
                    var endOfTurn = node["end_of_turn"]?.GetValue<bool>() ?? false;

                    if (endOfTurn)
                    {
                        // Always fire for final turns, including empty ones (silence).
                        // CompanionManager resolves the TCS immediately; empty string
                        // is treated as "nothing heard" upstream.
                        FinalTranscriptReceived?.Invoke(transcript);
                    }
                    else if (!string.IsNullOrWhiteSpace(transcript))
                    {
                        InterimTranscriptReceived?.Invoke(transcript);
                    }
                    break;

                case "Termination":
                    Logger.Log($"[ASR] Session terminated: audio={node["audio_duration_seconds"]}s");
                    break;

                default:
                    Logger.Log($"[ASR] Unknown message type: {type ?? "(null)"}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[ASR] Parse error: {ex.Message} — raw: {json}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Logger.Log($"[ASR] Disposing (msgs received: {_messagesReceived})");
        _cts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
            catch { }
        }
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
