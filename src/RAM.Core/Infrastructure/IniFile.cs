using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RAM.Core.Infrastructure;

/// <summary>Represents a property in an INI file.</summary>
public class IniProperty
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Represents a section in an INI file.</summary>
public class IniSection
{
    private readonly IDictionary<string, IniProperty> _properties;

    public string Name { get; set; }
    public IniProperty[] Properties => _properties.Values.ToArray();

    public IniSection(string name)
    {
        Name = name;
        _properties = new Dictionary<string, IniProperty>();
    }

    /// <summary>Checks if a property exists.</summary>
    public bool Exists(string name) => _properties.ContainsKey(name);

    /// <summary>Get a property value, coercing into the generic type. If the stored value
    /// cannot be converted (e.g. a hand-edited INI), the type's default is returned instead
    /// of throwing — a bad setting must never crash the app at startup.</summary>
    public T Get<T>(string name)
    {
        if (_properties.ContainsKey(name))
        {
            try
            {
                return (T)Convert.ChangeType(_properties[name].Value, typeof(T), CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
            {
                return default!;
            }
        }
        return default!;
    }

    public void Set(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            RemoveProperty(name);
            return;
        }

        if (!_properties.ContainsKey(name))
            _properties.Add(name, new IniProperty { Name = name, Value = value });
        else
            _properties[name].Value = value;
    }

    public void RemoveProperty(string propertyName)
    {
        if (_properties.ContainsKey(propertyName))
            _properties.Remove(propertyName);
    }
}

/// <summary>Represents an INI file that can be read from or written to.</summary>
public class IniFile
{
    private readonly object _saveLock = new();
    private readonly IDictionary<string, IniSection> _sections;

    public IniFile()
    {
        _sections = new Dictionary<string, IniSection>();
    }

    public IniFile(string path) : this() => Load(path);

    private void Load(string path)
    {
        using var file = new StreamReader(path);
        Load(file);
    }

    private void Load(TextReader reader)
    {
        IniSection? section = null;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();

            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith(";") || line.StartsWith("#")) continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                var sectionName = line.Substring(1, line.Length - 2);
                if (!_sections.ContainsKey(sectionName))
                {
                    section = new IniSection(sectionName);
                    _sections.Add(sectionName, section);
                }
                continue;
            }

            if (section != null)
            {
                var keyValue = line.Split(new[] { "=" }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (keyValue.Length != 2) continue;
                section.Set(keyValue[0].Trim(), keyValue[1].Trim());
            }
        }
    }

    /// <summary>Get a section by name. If the section doesn't exist, it is created.</summary>
    public IniSection Section(string sectionName)
    {
        if (!_sections.TryGetValue(sectionName, out var section))
        {
            section = new IniSection(sectionName);
            _sections.Add(sectionName, section);
        }
        return section;
    }

    /// <summary>
    /// Create a new INI file at the given path. Atomic write — the file is written to a
    /// <c>.tmp</c> sibling and renamed into place, the same pattern AccountStore uses, so a
    /// crash mid-write can never truncate the settings file (a truncated INI would silently
    /// reset every setting on next load).
    /// </summary>
    public void Save(string path)
    {
        lock (_saveLock)
        {
            string tmp = path + ".tmp";
            try
            {
                using (var file = new StreamWriter(tmp))
                    Save(file);
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            }
        }
    }

    private void Save(TextWriter writer)
    {
        foreach (var section in _sections.Values)
        {
            if (section.Properties.Length == 0) continue;

            writer.WriteLine($"[{section.Name}]");

            foreach (var property in section.Properties)
                writer.WriteLine("{0}={1}", property.Name, property.Value);

            writer.WriteLine();
        }
    }
}
