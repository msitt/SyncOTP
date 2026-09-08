using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SyncOTP.Core;

public sealed class NtfyConfig
{
    public string Server { get; set; } = "https://ntfy.sh";
    public string Topic { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>Last ntfy message id we processed, so a reconnect resumes instead of replaying.</summary>
    public string LastId { get; set; } = "";

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Server) &&
        !string.IsNullOrWhiteSpace(Topic);

    /// <summary>
    /// False on a public ntfy.sh topic, where the topic name is the only secret. Credentials are
    /// only sent when one of them is filled in.
    /// </summary>
    [JsonIgnore]
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(Username) || !string.IsNullOrWhiteSpace(Password);

    /// <summary>
    /// The exact value of the Authorization header, for both the PC and the iPhone Shortcut:
    /// <c>Basic base64(username + ":" + password)</c>. Empty when no credentials are configured.
    /// </summary>
    public string BuildBasicAuthHeader()
    {
        if (!HasCredentials) return "";
        var raw = Encoding.UTF8.GetBytes($"{Username}:{Password}");
        return "Basic " + Convert.ToBase64String(raw);
    }

    /// <summary>Full publish/subscribe URL for the configured topic. Derived, never persisted.</summary>
    [JsonIgnore]
    public string TopicUrl => $"{Server.TrimEnd('/')}/{Topic}";
}

public sealed class ClipboardConfig
{
    /// <summary>Wipe the clipboard this long after copying a code. 0 disables auto-clear.</summary>
    public int AutoClearSeconds { get; set; } = 120;

    /// <summary>Messages older than this when received are logged but never auto-copied.</summary>
    public int StaleAfterSeconds { get; set; } = 180;
}

public sealed class NotificationConfig
{
    public bool Toast { get; set; } = true;
    public bool Sound { get; set; } = false;
}

public sealed class ExtractorConfig
{
    /// <summary>Accept a lone number in a short message even with no verification keyword.</summary>
    public bool AcceptLowConfidence { get; set; } = true;
}

public sealed class Config
{
    public NtfyConfig Ntfy { get; set; } = new();
    public ClipboardConfig Clipboard { get; set; } = new();
    public NotificationConfig Notifications { get; set; } = new();
    public ExtractorConfig Extractor { get; set; } = new();

    /// <summary>Writes whole message bodies to the log. Off by default: bodies contain the codes.</summary>
    public bool VerboseLogging { get; set; } = false;

    /// <summary>
    /// How much of the message body to show in the log and the toast, so the service that sent
    /// the code can be told apart from the next one. 0 leaves the body out entirely.
    /// </summary>
    public int MessageSnippetChars { get; set; } = 120;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads config.json, creating it with defaults on first run.</summary>
    public static Config Load(out bool createdNew)
    {
        Paths.EnsureCreated();
        createdNew = false;

        if (!File.Exists(Paths.ConfigFile))
        {
            var fresh = new Config();
            fresh.Save();
            createdNew = true;
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(Paths.ConfigFile);
            return JsonSerializer.Deserialize<Config>(json, Options) ?? new Config();
        }
        catch (Exception ex)
        {
            FileLog.Error("config.json could not be parsed; using defaults", ex);
            return new Config();
        }
    }

    public void Save()
    {
        Paths.EnsureCreated();
        var json = JsonSerializer.Serialize(this, Options);

        // Write via a temp file so a crash mid-write cannot leave an empty config.
        var tmp = Paths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, Paths.ConfigFile, overwrite: true);
    }

    /// <summary>
    /// Persists only the resume cursor. Called often, and must not clobber edits the user made
    /// to config.json by hand while the app was running.
    /// </summary>
    public void SaveLastId(string lastId)
    {
        Ntfy.LastId = lastId;
        try
        {
            var onDisk = Load(out _);
            onDisk.Ntfy.LastId = lastId;
            onDisk.Save();
        }
        catch (Exception ex)
        {
            FileLog.Debug($"could not persist lastId: {ex.Message}");
        }
    }
}
