namespace AbilityDraft;

/// <summary>One hero ability that can be offered in the draft. <see cref="Slot"/> is its 1-4 position on the original hero.</summary>
public sealed record AbilityDef(string Hero, string HeroEn, string HeroRu, int Slot, string Name, string En, string Ru, bool Ult, string Icon)
{
    public string Title(bool ru) => ru ? Ru : En;
    public string HeroTitle(bool ru) => ru ? HeroRu : HeroEn;
}

public static partial class AbilityPool
{
    // Lazy: static field initializers of a partial class run in file order, and All lives in the generated file.
    static readonly Lazy<Dictionary<string, AbilityDef>> ByName = new(() => All.GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.First()));

    public static AbilityDef? Find(string name) => ByName.Value.GetValueOrDefault(name);

    public static IEnumerable<string> HeroNames => All.Select(a => a.Hero).Distinct();
}
