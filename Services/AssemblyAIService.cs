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

    // Audio arriving before the WebSocket is open is buffered here and flushed on
    // connect. Without this, the first ~0.5–1.5 s of speech (everything said while
    // the connection was still being established) was silently dropped — which is
    // why transcripts often missed the first word or two.
    private readonly Queue<byte[]> _preConnectBuffer = new();
    private const int MaxBufferedChunks = 150; // ≈ 9 s of audio — plenty
    private readonly object _bufferLock = new();

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

        // Flush any audio captured while we were still connecting, in order.
        byte[][] buffered;
        lock (_bufferLock)
        {
            buffered = _preConnectBuffer.ToArray();
            _preConnectBuffer.Clear();
        }
        if (buffered.Length > 0)
        {
            Logger.Log($"[ASR] Flushing {buffered.Length} pre-connect audio chunk(s)");
            foreach (var chunk in buffered)
                await _ws.SendAsync(new ArraySegment<byte>(chunk), WebSocketMessageType.Binary, true, _cts.Token);
        }
    }

    public async Task SendAudioAsync(byte[] pcm16Chunk, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open)
        {
            // Not connected yet — buffer so the start of the sentence isn't lost.
            lock (_bufferLock)
            {
                if (_preConnectBuffer.Count < MaxBufferedChunks)
                    _preConnectBuffer.Enqueue(pcm16Chunk);
            }
            return;
        }
        await _ws.SendAsync(new ArraySegment<byte>(pcm16Chunk), WebSocketMessageType.Binary, true, ct);
    }

    public async Task FinalizeAsync(CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open)
        {
            Logger.Log("[ASR] FinalizeAsync: WebSocket not open");
            return;
        }
        // v3 control message — force end of the current turn. The protocol requires a
        // "type" field: the old {"force_end_utterance":true} shape was rejected by the
        // server with error 3006 ("Invalid Message Type") on EVERY session, killing the
        // socket instead of finalizing the turn.
        var msg = JsonSerializer.Serialize(new { type = "ForceEndpoint" });
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
