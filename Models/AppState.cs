namespace ClickyWindows.Models;

public enum AppState
{
    Idle,
    Listening,   // push-to-talk key held, mic active
    Processing,  // key released, transcription → Claude in flight
    Speaking,    // TTS playing back
}
