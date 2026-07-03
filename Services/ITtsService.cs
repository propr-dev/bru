namespace ClickyWindows.Services;

/// <summary>
/// Common surface for a text-to-speech engine, so CompanionManager can use either the
/// free Windows voice or ElevenLabs interchangeably based on the TtsEngine setting.
/// </summary>
public interface ITtsService : IDisposable
{
    /// <summary>Fires immediately before audio playback begins (drives the Speaking state).</summary>
    event Action? PlaybackStarting;

    bool IsPlaying { get; }

    /// <summary>Synthesizes and plays the text; returns when playback completes or is cancelled.</summary>
    Task SpeakAsync(string text, CancellationToken ct = default);

    /// <summary>Cuts off any current playback.</summary>
    void StopPlayback();
}
