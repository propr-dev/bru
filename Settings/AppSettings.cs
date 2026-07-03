using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClickyWindows.Settings;

public class AppSettings
{
    // API keys — supplied by %APPDATA%\ClickyWindows\settings.json at runtime.
    // NEVER put real keys here: these defaults are compiled into the executable.
    public string AnthropicApiKey { get; set; } = "";
    public string ElevenLabsApiKey { get; set; } = "";
    public string ElevenLabsVoiceId { get; set; } = "";
    public string AssemblyAiApiKey { get; set; } = "";

    // Optional Cloudflare proxy URLs (leave empty to call APIs directly)
    public string ClaudeProxyUrl { get; set; } = "";
    public string ElevenLabsProxyUrl { get; set; } = "";
    public string AssemblyAiTokenUrl { get; set; } = "";

    // Push-to-talk hotkey — default: Ctrl+Space
    public uint HotkeyModifiers { get; set; } = 0x0002; // MOD_CONTROL
    public uint HotkeyVirtualKey { get; set; } = 0x20; // VK_SPACE

    // Claude model for conversation.
    public string ClaudeModel { get; set; } = "claude-sonnet-4-6";
    // Model used ONLY for the precise pointing (Computer Use) call. Stronger = more
    // accurate aiming. Kept separate so chat can run on a cheaper model while pointing
    // stays sharp. Leave empty to reuse ClaudeModel.
    public string PointingModel { get; set; } = "claude-sonnet-4-6";

    // Which voice engine: "windows" (free, offline) or "elevenlabs" (paid, needs a key + usable voice id).
    public string TtsEngine { get; set; } = "windows";

    // Overlay settings
    public bool ShowCursorOverlay { get; set; } = true;

    // Windows built-in voice (free TTS). Rate: -10 (slow) .. 10 (fast). Volume: 0..100.
    public int WindowsTtsRate { get; set; } = 0;
    public int WindowsTtsVolume { get; set; } = 100;
    // Name (or part of it) of the installed Windows voice to use, e.g. "Zira", "David", "Mark".
    // Leave empty for the system default. Installed voices are logged at startup in clicky.log.
    public string WindowsTtsVoice { get; set; } = "";

    // File access (tool use). Clicky can list/find and copy files, but ONLY inside
    // AllowedFolders. No move, no delete. Copies require spoken confirmation.
    public bool FileAccessEnabled { get; set; } = true;
    public string[] AllowedFolders { get; set; } =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "BRU"),
    ];

    // ── Persistence ────────────────────────────────────────────────────────

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClickyWindows",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
        }
        catch { /* ignore, use defaults */ }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* ignore */ }
    }

    // Convenience: which API to call for Claude
    public string ClaudeApiUrl =>
        !string.IsNullOrWhiteSpace(ClaudeProxyUrl)
            ? ClaudeProxyUrl
            : "https://api.anthropic.com/v1/messages";

    public string ElevenLabsApiUrl(string voiceId) =>
        !string.IsNullOrWhiteSpace(ElevenLabsProxyUrl)
            ? ElevenLabsProxyUrl
            : $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}";
}
