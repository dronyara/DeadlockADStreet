using System.Text;
using System.Text.Json;
using DeadworksManaged.Api;

namespace AbilityDraft;

// The server owner's settings: AbilityDraft.config.json next to the plugin DLL. Read at every map start, so an edit takes
// effect in the next lobby without restarting the server.
public sealed partial class DraftPlugin
{
    sealed class ConfigFile
    {
        /// <summary>Abilities never offered in the draft: internal names, or the English or Russian names shown in game.</summary>
        public List<string> Blacklist { get; set; } = new();
        /// <summary>Language of the plugin's messages: "en" or "ru". A player can still switch their own with /lang.</summary>
        public string Language { get; set; } = "en";
        /// <summary>How many players the lobby takes. More than MaxPlayersStreetBrawl can only play Standard.</summary>
        public int MaxPlayersStandard { get; set; } = 12;
        public int MaxPlayersStreetBrawl { get; set; } = 8;
        /// <summary>No two players on the same hero.</summary>
        public bool UniqueHeroes { get; set; } = true;
        /// <summary>After a match ends: seconds until everyone but the lobby leader is kicked and the lobby reopens. 0 = never.</summary>
        public int SecondsAfterMatch { get; set; } = 20;
    }

    ConfigFile _config = new();
    static bool _ru;                // the configured language; seats start with it

    /// <summary>A message in the configured language.</summary>
    static string L(string ru, string en) => _ru ? ru : en;

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
                WriteConfig(new ConfigFile());
            WriteAbilityList();

            string text = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<ConfigFile>(text, ConfigJson) ?? new ConfigFile();
            // A config written by an older version: add the settings it does not have yet, keep the ones it has.
            if (!text.Contains(nameof(ConfigFile.SecondsAfterMatch), StringComparison.OrdinalIgnoreCase)) WriteConfig(config);
            _config = config;
            _ru = config.Language.Trim().StartsWith("ru", StringComparison.OrdinalIgnoreCase);
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
            Log($"config: language {(_ru ? "ru" : "en")}, players {config.MaxPlayersStandard} (Street Brawl {config.MaxPlayersStreetBrawl}), unique heroes {config.UniqueHeroes}, " +
                $"{_blacklist.Count} abilities blacklisted{(_blacklist.Count > 0 ? " (" + string.Join(", ", _blacklist) + ")" : "")}" +
                (unknown.Count > 0 ? $"; NOT RECOGNISED: {string.Join(", ", unknown)}" : ""));
        }
        catch (Exception ex)
        {
            Log($"config: could not read {ConfigPath} ({ex.Message}) - no blacklist applied");
        }
    }

    static void WriteConfig(ConfigFile config) =>
        File.WriteAllText(ConfigPath, "// Ability Draft settings. Read at every map start and on /draft.\n" +
            "// Blacklist: abilities that are never offered. Use the internal name or the English / Russian name\n" +
            "//   from AbilityDraft.abilities.txt, for example: \"Blacklist\": [\"Hotel Guest\", \"ability_frank_revive\"]\n" +
            "// Language: \"en\" or \"ru\" - the language of the plugin's messages.\n" +
            "// MaxPlayersStandard / MaxPlayersStreetBrawl: how many players the lobby takes; with more than the\n" +
            "//   Street Brawl number in the lobby only Standard can be played.\n" +
            "// UniqueHeroes: true - a hero somebody already has cannot be picked.\n" +
            "// SecondsAfterMatch: after a match ends, seconds until everyone but the lobby leader is kicked and the\n" +
            "//   lobby reopens. 0 switches this off.\n" +
            JsonSerializer.Serialize(config, ConfigJson) + "\n", new UTF8Encoding(false));

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
