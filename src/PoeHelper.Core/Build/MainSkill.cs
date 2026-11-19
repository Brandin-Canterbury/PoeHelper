using System.Text.RegularExpressions;

namespace PoeHelper.Core.Build;

/// <summary>
/// The skill the build is about, read from the gems: the active gem with the most supports linked to it.
/// Its tags decide which damage stats count (a cold spell doesn't care about "increased Physical Damage").
/// </summary>
public sealed record SkillProfile(GemInfo Gem, string Slot, int LinkedSupports, bool Auto)
{
    public string Name => Gem.Name;
    public IReadOnlyList<string> Tags => Gem.Tags ?? [];
    public bool Has(string tag) => Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
    public bool IsMinion => Has("Minion");
    public bool IsAttack => Has("Attack") && !IsMinion;
    public bool IsSpell => Has("Spell") && !IsMinion && !IsAttack;
    public bool IsDot { get; init; }

    /// <summary>The damage types the skill deals: its element tags, or physical for an untagged attack.</summary>
    public IReadOnlyList<string> Elements
    {
        get
        {
            var e = MainSkill.DamageTypes.Where(Has).ToList();
            if (e.Count == 0 && (IsAttack || IsMinion)) e.Add("Physical");
            return e;
        }
    }

    public string Describe() =>
        $"{Name} ({string.Join(", ", new[] { IsMinion ? "minion" : IsAttack ? "attack" : IsSpell ? "spell" : "skill", IsDot ? "damage over time" : "hit" }.Concat(Elements.Select(e => e.ToLowerInvariant())))})";
}

public static partial class MainSkill
{
    public static readonly string[] DamageTypes = ["Fire", "Cold", "Lightning", "Physical", "Chaos"];

    /// <summary>Gems that are never "the build": auras, curses, movement, warcries, golems and the like.</summary>
    static readonly string[] UtilityTags =
        ["Aura", "Herald", "Curse", "Hex", "Mark", "Travel", "Blink", "Warcry", "Guard", "Golem", "Banner", "Stance", "Link"];

    /// <summary>Supports that only make sense on a damage-over-time skill.</summary>
    static readonly string[] DotSupports =
        ["Swift Affliction", "Burning Damage", "Deadly Ailments", "Unbound Ailments", "Decay", "Cruelty"];

    public static SkillProfile? Detect(CharacterBuild b, string? overrideName = null)
    {
        var candidates = new List<(GemInfo Gem, string Slot, int Supports)>();
        foreach (var it in b.Items.Values)
            foreach (var g in it.Gems.Where(g => !g.Support))
            {
                int supports = it.Gems.Count(s => s.Support && s.Group == g.Group);
                candidates.Add((g, it.Slot, supports));
            }
        if (candidates.Count == 0) return null;

        bool auto = true;
        var pick = !string.IsNullOrWhiteSpace(overrideName)
            ? candidates.FirstOrDefault(c => c.Gem.Name.Contains(overrideName.Trim(), StringComparison.OrdinalIgnoreCase))
            : default;
        if (pick.Gem is not null) auto = false;
        else
        {
            bool Utility(GemInfo g) => g.Tags is { } t && t.Any(x => UtilityTags.Contains(x, StringComparer.OrdinalIgnoreCase));
            pick = candidates.OrderBy(c => Utility(c.Gem) ? 1 : 0)
                             .ThenByDescending(c => c.Supports)
                             .ThenByDescending(c => c.Gem.Level)
                             .First();
        }

        var item = b.Items[pick.Slot];
        var linked = item.Gems.Where(s => s.Support && s.Group == pick.Gem.Group).ToList();
        bool dot = pick.Gem.Mods.Any(m => DotRx().IsMatch(m))
                   || linked.Count(s => DotSupports.Any(d => s.Name.StartsWith(d))) >= 1;
        return new SkillProfile(pick.Gem, pick.Slot, pick.Supports, auto) { IsDot = dot };
    }

    [GeneratedRegex(@"Base \w+ Damage per second|Damage over Time|Damage per second", RegexOptions.IgnoreCase)]
    private static partial Regex DotRx();
}
