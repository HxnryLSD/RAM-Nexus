using Newtonsoft.Json;

namespace RAM.Core.Models;

/// <summary>
/// A Roblox account. Data-only model (no HTTP, no UI). Roblox API interactions
/// are performed through <see cref="RAM.Core.Roblox.RobloxApiClient"/>.
/// Serialization shape is kept compatible with the upstream AccountData.json.
/// </summary>
public class Account
{
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool Valid;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? SecurityToken;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Username;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public DateTime LastUse;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string Group { get; set; } = "Default";

    public long UserID;

    public Dictionary<string, string> Fields = new();

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public DateTime LastAttemptedRefresh;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? BrowserTrackerID;

    private string? _alias;
    private string? _description;
    private string? _password;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Alias
    {
        get => _alias;
        set
        {
            if (value is null) { _alias = null; return; }
            if (value.Length > 50) throw new ArgumentOutOfRangeException(nameof(value), "Alias cannot exceed 50 characters.");
            _alias = value;
        }
    }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Description
    {
        get => _description;
        set
        {
            if (value is null) { _description = null; return; }
            if (value.Length > 5000) throw new ArgumentOutOfRangeException(nameof(value), "Description cannot exceed 5000 characters.");
            _description = value;
        }
    }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Password
    {
        get => _password;
        set
        {
            if (value is null) { _password = null; return; }
            if (value.Length > 5000) throw new ArgumentOutOfRangeException(nameof(value), "Password cannot exceed 5000 characters.");
            _password = value;
        }
    }

    public Account() { }

    public Account(string cookie) => SecurityToken = cookie;

    /// <summary>Display name: alias if set, otherwise username.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrEmpty(Alias) ? Username ?? "" : Alias;

    /// <summary>True when the account has a stored (vault) password. UI hint only — not serialized.</summary>
    [JsonIgnore]
    public bool HasStoredPassword => !string.IsNullOrEmpty(Password);

    /// <summary>True when the account has notes (description). UI hint only — not serialized.</summary>
    [JsonIgnore]
    public bool HasNotes => !string.IsNullOrEmpty(Description);
}
