using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClickyWindows.Settings;
using NAudio.Wave;

namespace ClickyWindows.Services;

/// <summary>
/// Synthesizes speech via ElevenLabs TTS API and plays it back using NAudio.
/// Mirrors the ElevenLabsTTSClient in macOS Clicky.
/// </summary>
public class ElevenLabsService : ITtsService
{
    private static readonly HttpClient Http = new();
    private readonly AppSettings _settings;
    private IWavePlayer? _player;
    private Mp3FileReader? _reader;

    public bool IsPlaying => _player?.PlaybackState == PlaybackState.Playing;

    /// <summary>
    /// Fires on the thread pool immediately before audio playback begins —
    /// after the MP3 has been fetched and decoded. CompanionManager transitions
    /// to AppState.Speaking here so the spinner runs through the entire HTTP fetch.
    /// </summary>
    public event Action? PlaybackStarting;

    public ElevenLabsService(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Synthesizes text to speech and plays it. Returns when playback completes.
    /// </summary>
    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        byte[] mp3 = await FetchAudioAsync(text, ct);
        await PlayMp3Async(mp3, ct);
    }

    public void StopPlayback()
    {
        _player?.Stop();
    }

    private async Task<byte[]> FetchAudioAsync(string text, CancellationToken ct)
    {
        var voiceId = _settings.ElevenLabsVoiceId;
        var url = _settings.ElevenLabsApiUrl(voiceId);

        var body = new
        {
            text,
            model_id = "eleven_flash_v2_5",
            voice_settings = new
            {
                stability = 0.5,
                similarity_boost = 0.75,
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(_settings.ElevenLabsApiKey))
            request.Headers.Add("xi-api-key", _settings.ElevenLabsApiKey);

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

        var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new Exception($"ElevenLabs error {(int)response.StatusCode}: {err}");
        }

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task PlayMp3Async(byte[] mp3Bytes, CancellationToken ct)
    {
        // Dispose previous player
        _player?.Dispose();
        _reader?.Dispose();

        var ms = new MemoryStream(mp3Bytes);
        _reader = new Mp3FileReader(ms);
        _player = new WasapiOut();
        _player.Init(_reader);

        var tcs = new TaskCompletionSource<bool>();
        _player.PlaybackStopped += (_, _) => tcs.TrySetResult(true);

        using var reg = ct.Register(() =>
        {
            _player.Stop();
            tcs.TrySetCanceled();
        });

        PlaybackStarting?.Invoke();
        _player.Play();
        await tcs.Task;
    }

    public void Dispose()
    {
        _player?.Dispose();
        _reader?.Dispose();
    }
}
