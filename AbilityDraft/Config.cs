using System.Text;
using System.Text.Json;
using DeadworksManaged.Api;

namespace AbilityDraft;

// The host's settings: AbilityDraft.config.json next to the plugin DLL. Read at every map start, so an edit takes
// effect in the next lobby without restarting the server.
public sealed partial class DraftPlugin
{
    sealed class ConfigFile
    {
        /// <summary>Abilities never offered in the draft: internal names, or the English or Russian names shown in game.</summary>
        public List<string> Blacklist { get; set; } = new();
    }

    static readonly JsonSerializerOptions ConfigJson = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
    };

    static string PluginDir => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "managed", "plugins");
    static string ConfigPath => Path.Combine(PluginDir, "AbilityDraft.config.json");
    static string AbilityListPath => Path.Combine(PluginDir, "AbilityDraft.abilities.txt");

    readonly HashSet<string> _blacklist = new(StringComparer.OrdinalIgnoreCase);     // internal ability names

    /// <summary>Everything the draft may offer: the generated pool minus the host's blacklist.</summary>
    IEnumerable<AbilityDef> Pool => AbilityPool.All.Where(a => !_blacklist.Contains(a.Name));

    void LoadConfig()
    {
        _blacklist.Clear();
        try
        {
            if (!File.Exists(ConfigPath))
                File.WriteAllText(ConfigPath, "// Ability Draft settings. Read at every map start.\n" +
                    "// blacklist: abilities that are never offered. Use the internal name or the English / Russian name\n" +
                    "// from AbilityDraft.abilities.txt, for example: \"blacklist\": [\"Hotel Guest\", \"ability_frank_revive\"]\n" +
                    JsonSerializer.Serialize(new ConfigFile(), ConfigJson) + "\n", new UTF8Encoding(false));
            WriteAbilityList();

            var config = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(ConfigPath), ConfigJson) ?? new ConfigFile();
            var unknown = new List<string>();
            foreach (var entry in config.Blacklist.Select(e => e.Trim()).Where(e => e.Length > 0))
            {
                var hits = AbilityPool.All.Where(a =>
                    a.Name.Equals(entry, StringComparison.OrdinalIgnoreCase) ||
                    a.En.Equals(entry, StringComparison.OrdinalIgnoreCase) ||
                    a.Ru.Equals(entry, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 0) unknown.Add(entry);
                foreach (var a in hits) _blacklist.Add(a.Name);
            }
            Log($"config: {_blacklist.Count} abilities blacklisted{(_blacklist.Count > 0 ? " (" + string.Join(", ", _blacklist) + ")" : "")}" +
                (unknown.Count > 0 ? $"; NOT RECOGNISED: {string.Join(", ", unknown)}" : ""));
        }
        catch (Exception ex)
        {
            Log($"config: could not read {ConfigPath} ({ex.Message}) - no blacklist applied");
        }
    }

    /// <summary>A plain list of every ability name the blacklist accepts, kept next to the config for the host.</summary>
    static void WriteAbilityList()
    {
        var sb = new StringBuilder("internal name | English | Russian | hero | ultimate\n");
        foreach (var a in AbilityPool.All.OrderBy(a => a.HeroEn).ThenBy(a => a.Slot))
            sb.Append($"{a.Name} | {a.En} | {a.Ru} | {a.HeroEn} | {(a.Ult ? "yes" : "no")}\n");
        var text = sb.ToString();
        if (!File.Exists(AbilityListPath) || File.ReadAllText(AbilityListPath) != text)
            File.WriteAllText(AbilityListPath, text, new UTF8Encoding(false));
    }
}
