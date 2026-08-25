using System.IO;
using Newtonsoft.Json;

namespace RAM.Core.Roblox.FastFlags;

/// <summary>
/// Persists the user's activated fast flags (name -> string value) to a JSON file.
/// A flag present in the map is "activated"; removing it deactivates it.
/// </summary>
public sealed class FastFlagStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _values;

    public string FilePath => _path;

    /// <summary>
    /// Default path is anchored next to the executable (not the process working directory) so
    /// the flag file is the same no matter how the app is launched. Tests pass explicit paths.
    /// </summary>
    public FastFlagStore(string? path = null)
    {
        _path = path ?? AppPaths.FastFlags;
        _values = LoadFromDisk();
    }

    private Dictionary<string, string> LoadFromDisk()
    {
        if (!File.Exists(_path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var data = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(_path));
            return data ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>All currently activated flags (name -> serialized value).</summary>
    public IReadOnlyDictionary<string, string> GetActivated()
        => new Dictionary<string, string>(_values, StringComparer.Ordinal);

    public string? Get(string name)
        => _values.TryGetValue(name, out var v) ? v : null;

    public bool IsActivated(string name) => _values.ContainsKey(name);

    /// <summary>Activate a flag and set its value, then persist.</summary>
    public void Set(string name, string value)
    {
        _values[name] = value;
        Save();
    }

    /// <summary>Deactivate a flag and persist.</summary>
    public void Remove(string name)
    {
        _values.Remove(name);
        Save();
    }

    public void Save()
    {
        var ordered = _values
            .OrderBy(k => k.Key, StringComparer.Ordinal)
            .ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal);
        File.WriteAllText(_path, JsonConvert.SerializeObject(ordered, Newtonsoft.Json.Formatting.Indented));
    }
}
