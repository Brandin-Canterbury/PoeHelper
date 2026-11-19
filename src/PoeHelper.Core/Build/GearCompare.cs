using System.Globalization;
using PoeHelper.Core.Items;

namespace PoeHelper.Core.Build;

public enum Sentiment { Neutral, Better, Worse, Bad }

public sealed record CompareRow(string Label, string Before, string After, string Delta, Sentiment Sentiment);

public sealed class SlotComparison
{
    public required string Slot { get; init; }
    public required string SlotLabel { get; init; }
    public EquippedItem? Current { get; init; }
    public required BuildStats Before { get; init; }
    public required BuildStats After { get; init; }
    public List<CompareRow> Rows { get; } = [];
    public List<string> Warnings { get; } = [];
    public string Verdict { get; set; } = "";
    public Sentiment Overall { get; set; }
    public EquippedItem? Candidate { get; init; }
    /// <summary>Life % + damage % change: the single number upgrades are ranked by.</summary>
    public double Score { get; set; }
}

/// <summary>"What if the hovered item replaced the one in that slot?" against the character model.</summary>
public static class GearCompare
{
    public static IReadOnlyList<string> SlotsFor(ParsedItem item)
    {
        var c = item.ItemClass;
        return c switch
        {
            "Body Armours" => ["BodyArmour"],
            "Helmets" => ["Helm"],
            "Gloves" => ["Gloves"],
            "Boots" => ["Boots"],
            "Belts" => ["Belt"],
            "Amulets" => ["Amulet"],
            "Rings" => ["Ring", "Ring2"],
            "Shields" or "Quivers" => ["Offhand"],
            _ when item.IsWeapon => ["Weapon"],
            _ => [],
        };
    }

    /// <summary>Overcap below this after a resist drop gets a warning: one curse or map mod uncaps it.</summary>
    public const double ThinOvercap = 15;

    public static string SlotLabel(string slot) => slot switch
    {
        "BodyArmour" => "body armour",
        "Helm" => "helmet",
        "Ring" => "left ring",
        "Ring2" => "right ring",
        "Offhand" => "off-hand",
        _ => slot.ToLowerInvariant(),
    };

    public static List<SlotComparison> Compare(CharacterBuild build, ParsedItem item)
    {
        var before = BuildCalculator.Compute(build);
        var result = new List<SlotComparison>();
        foreach (var slot in SlotsFor(item))
        {
            build.Items.TryGetValue(slot, out var current);
            var (candidate, _) = ToEquipped(slot, item, current);
            result.Add(CompareEquipped(build, slot, candidate, ItemCategories.IsTwoHanded(item.ItemClass), before));
        }
        return result;
    }

    /// <summary>Compares any candidate item (clipboard or trade listing) for one slot. The slot's gems move into it.</summary>
    public static SlotComparison CompareEquipped(CharacterBuild build, string slot, EquippedItem candidate, bool twoHanded = false, BuildStats? before = null)
    {
        before ??= BuildCalculator.Compute(build);
        build.Items.TryGetValue(slot, out var current);
        var gems = current?.Gems ?? [];
        int fit = Math.Min(gems.Count, candidate.SocketCount);
        var moved = new EquippedItem
        {
            Slot = slot, Name = candidate.Name, ItemClass = candidate.ItemClass, Mods = candidate.Mods,
            ReqStr = candidate.ReqStr, ReqDex = candidate.ReqDex, ReqInt = candidate.ReqInt,
            SocketCount = candidate.SocketCount, MaxLinks = candidate.MaxLinks, Gems = gems.Take(fit).ToList(), Raw = candidate.Raw,
            Props = candidate.Props,
        };
        var swapped = build.WithItem(slot, moved);
        if (twoHanded && slot == "Weapon" && build.Items.ContainsKey("Offhand"))
            swapped = swapped.WithItem("Offhand", null);
        var after = BuildCalculator.Compute(swapped);
        var cmp = new SlotComparison { Slot = slot, SlotLabel = SlotLabel(slot), Current = current, Before = before, After = after, Candidate = moved };
        Fill(cmp, before, after, gems.Skip(fit).ToList(), current, moved, twoHanded, build);
        return cmp;
    }

    /// <summary>The hovered item as if equipped: gems move across while sockets allow.</summary>
    public static (EquippedItem Item, List<GemInfo> Lost) ToEquipped(string slot, ParsedItem item, EquippedItem? current)
    {
        var gems = current?.Gems ?? [];
        int fit = Math.Min(gems.Count, item.SocketCount);
        var mods = item.Mods.Where(m => !(m.Text.StartsWith('(') && m.Text.EndsWith(')'))).Select(m => m.Text).ToList();
        var eq = new EquippedItem
        {
            Slot = slot, Name = item.DisplayName, ItemClass = item.ItemClass, Mods = mods,
            ReqStr = item.Requirements.Str, ReqDex = item.Requirements.Dex, ReqInt = item.Requirements.Int,
            SocketCount = item.SocketCount, MaxLinks = item.MaxLinks, Gems = gems.Take(fit).ToList(),
            Props = new Dictionary<string, string>(item.Properties, StringComparer.OrdinalIgnoreCase),
        };
        return (eq, gems.Skip(fit).ToList());
    }

    static void Fill(SlotComparison c, BuildStats b, BuildStats a, List<GemInfo> lost, EquippedItem? current, EquippedItem candidate, bool twoHanded, CharacterBuild build)
    {
        // Life
        c.Rows.Add(Num("Life", b.Life, a.Life, higherIsBetter: true));

        // Resists: what matters is the capped value; overcap is a buffer.
        foreach (var (name, rb, ra) in new[] { ("Fire res", b.Fire, a.Fire), ("Cold res", b.Cold, a.Cold), ("Lightning res", b.Lightning, a.Lightning), ("Chaos res", b.Chaos, a.Chaos) })
        {
            // Bad only when an elemental resist falls under its cap; a lower cap or less chaos res is just worse.
            bool fellUnder = rb.Capped && !ra.Capped && !name.StartsWith("Chaos");
            var s = fellUnder ? Sentiment.Bad
                  : ra.Effective < rb.Effective ? Sentiment.Worse
                  : ra.Effective > rb.Effective ? Sentiment.Better
                  : Sentiment.Neutral;
            string Fmt(Resist r) => $"{r.Effective:0}%" + (r.Capped && r.Overcap > 0 ? $" (+{r.Overcap:0})" : r.Capped ? "" : $" / {r.Max:0}");
            double d = ra.Total - rb.Total;
            c.Rows.Add(new CompareRow(name, Fmt(rb), Fmt(ra), d == 0 ? "" : $"{d:+0;-0}", s));
            if (fellUnder)
                c.Warnings.Add($"{name} drops below cap: {ra.Effective:0}% of {ra.Max:0}% ({ra.Total - ra.Max:0}).");
            else if (!name.StartsWith("Chaos") && d < 0 && ra.Capped && ra.Overcap < ThinOvercap)
                c.Warnings.Add($"{name} stays capped, but only {ra.Overcap:0}% over ({rb.Overcap:0}% now). A curse or a map mod that lowers res will uncap it.");
        }

        // Attributes against gem / gear requirements.
        foreach (var (name, nb, na) in new[] { ("Str", b.StrNeed, a.StrNeed), ("Dex", b.DexNeed, a.DexNeed), ("Int", b.IntNeed, a.IntNeed) })
        {
            var s = !na.Met ? Sentiment.Bad : na.Have > nb.Have ? Sentiment.Better : na.Have < nb.Have ? Sentiment.Worse : Sentiment.Neutral;
            double d = na.Have - nb.Have;
            c.Rows.Add(new CompareRow($"{name} (need {na.Need})", $"{nb.Have:0}", $"{na.Have:0}", d == 0 ? "" : $"{d:+0;-0}", s));
            if (!na.Met)
                c.Warnings.Add($"{name} {na.Have:0} is short of {na.Need} needed by {na.NeededBy}.");
        }

        double dmgChange = DamageChangePct(b, a);
        var skill = b.Skill;
        c.Rows.Add(new CompareRow(skill is null ? "Damage" : $"{skill.Name} damage", "", "",
            Math.Abs(dmgChange) < 0.05 ? "" : $"{dmgChange:+0.0;-0.0}%",
            dmgChange > 0.05 ? Sentiment.Better : dmgChange < -0.05 ? Sentiment.Worse : Sentiment.Neutral));
        bool dot = skill is null || skill.IsDot;
        if (skill is not { IsMinion: true })
            AddIfChanged(c, dot ? "DoT multiplier" : "Crit multiplier", b.DamageMulti, a.DamageMulti, "%");
        AddIfChanged(c, skill is { IsMinion: true } ? "Minion damage" : "Increased damage", b.IncreasedDamage, a.IncreasedDamage, "%");
        if (skill is { IsDot: false })
        {
            AddIfChanged(c, "Crit chance", b.CritChance, a.CritChance, "%");
            if (Math.Abs(a.Speed - b.Speed) > 0.005)
                c.Rows.Add(new CompareRow(skill.IsMinion ? "Minion speed" : skill.IsAttack ? "Attacks /s" : "Cast speed (rel.)",
                    b.Speed.ToString("0.00", CultureInfo.InvariantCulture), a.Speed.ToString("0.00", CultureInfo.InvariantCulture),
                    $"{(a.Speed / b.Speed - 1) * 100:+0.#;-0.#}%", a.Speed > b.Speed ? Sentiment.Better : Sentiment.Worse));
        }
        AddIfChanged(c, "Energy shield", b.EnergyShield, a.EnergyShield, "");
        AddIfChanged(c, "Movement speed", b.MovementSpeed, a.MovementSpeed, "%");
        AddIfChanged(c, "+Skill gem levels", b.SkillGemLevels, a.SkillGemLevels, "");

        // Sockets and links: losing the main skill's links is the worst outcome.
        if (current is { Gems.Count: > 0 })
        {
            if (lost.Count > 0)
                c.Warnings.Add($"Only {candidate.SocketCount} socket(s): {string.Join(", ", lost.Select(g => g.Name))} wouldn't fit.");
            if (candidate.MaxLinks < current.MaxLinks && current.MaxLinks >= 3)
                c.Warnings.Add($"Links drop from {current.MaxLinks}L to {candidate.MaxLinks}L"
                               + (b.Skill is { } sk && current.Gems.Any(g => g.Name == sk.Name) ? $": your {sk.Name} setup is in this slot." : "."));
        }
        if (twoHanded && build.Items.ContainsKey("Offhand"))
            c.Warnings.Add("Two-handed: your off-hand would be unequipped (included above).");

        Summarize(c, b, a, dmgChange);
    }

    static void Summarize(SlotComparison c, BuildStats b, BuildStats a, double dmgChange)
    {
        bool broken = c.Rows.Any(r => r.Sentiment == Sentiment.Bad) || c.Warnings.Any(w => w.Contains("wouldn't fit") || w.Contains("Links drop"));
        double lifePct = b.Life > 0 ? (a.Life / b.Life - 1) * 100 : 0;
        var gains = new List<string>();
        var losses = new List<string>();
        void Note(double v, string pos, string neg) { if (v > 0) gains.Add(pos); else if (v < 0) losses.Add(neg); }
        Note(a.Life - b.Life, $"+{a.Life - b.Life:0} life", $"{a.Life - b.Life:0} life");
        if (Math.Abs(dmgChange) >= 0.5) Note(dmgChange, $"+{dmgChange:0.#}% damage", $"{dmgChange:0.#}% damage");

        // One number to rank: life % + damage %, but anything that breaks the build wins.
        double score = lifePct + dmgChange;
        c.Score = score;
        if (broken)
        {
            c.Overall = Sentiment.Bad;
            c.Verdict = "Don't swap: " + ShortReason(c);
        }
        else if (score > 0.5)
        {
            c.Overall = Sentiment.Better;
            c.Verdict = "Upgrade: " + string.Join(", ", gains) + (losses.Count > 0 ? $" (but {string.Join(", ", losses)})" : "") + ". Resists and attributes hold.";
        }
        else if (score < -0.5)
        {
            c.Overall = Sentiment.Worse;
            c.Verdict = "Downgrade: " + string.Join(", ", losses) + (gains.Count > 0 ? $" (gains {string.Join(", ", gains)})" : "") + ".";
        }
        else
        {
            c.Overall = Sentiment.Neutral;
            c.Verdict = "Sidegrade: about the same" + (gains.Count + losses.Count > 0 ? $" ({string.Join(", ", gains.Concat(losses))})" : "") + ".";
        }
    }

    static string ShortReason(SlotComparison c)
    {
        if (c.Warnings.Any(w => w.Contains(" setup is in this slot"))) return "it breaks your main skill's links.";
        if (c.Warnings.Any(w => w.Contains("wouldn't fit"))) return "your socketed gems wouldn't fit.";
        var bad = c.Rows.FirstOrDefault(r => r.Sentiment == Sentiment.Bad);
        return bad is null ? "it breaks a requirement." : bad.Label switch
        {
            var l when l.EndsWith(" res") => $"{l} drops below cap.",
            var l => $"{l.Split(' ')[0]} requirement not met.",
        };
    }

    /// <summary>Main-skill damage change in %, from the relative damage index.</summary>
    public static double DamageChangePct(BuildStats b, BuildStats a) =>
        b.DamageIndex > 0 ? (a.DamageIndex / b.DamageIndex - 1) * 100 : 0;

    static CompareRow Num(string label, double before, double after, bool higherIsBetter)
    {
        double d = after - before;
        var s = Math.Abs(d) < 0.5 ? Sentiment.Neutral : (d > 0) == higherIsBetter ? Sentiment.Better : Sentiment.Worse;
        return new CompareRow(label, F(before), F(after), Math.Abs(d) < 0.5 ? "" : $"{d:+0;-0}", s);
    }

    static void AddIfChanged(SlotComparison c, string label, double before, double after, string unit)
    {
        if (Math.Abs(after - before) < 0.01) return;
        double d = after - before;
        c.Rows.Add(new CompareRow(label, F(before) + unit, F(after) + unit, $"{d:+0.#;-0.#}{unit}", d > 0 ? Sentiment.Better : Sentiment.Worse));
    }

    static string F(double v) => v.ToString("0", CultureInfo.InvariantCulture);
}
