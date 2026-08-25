using Newtonsoft.Json;

namespace RAM.Core;

/// <summary>Pure, UI-agnostic helpers ported from the upstream Utilities.cs (WinForms-only parts omitted).</summary>
public static class Utilities
{
    public static bool TryParseJson<T>(string? json, out T result)
    {
        bool success = true;
        var settings = new JsonSerializerSettings
        {
            Error = (sender, args) => { success = false; args.ErrorContext.Handled = true; },
            MissingMemberHandling = MissingMemberHandling.Error
        };
        if (string.IsNullOrEmpty(json)) { result = default!; return false; }
        result = JsonConvert.DeserializeObject<T>(json, settings)!;
        return success;
    }
}
