namespace PoeHelper.Core.Items;

public enum ItemRarity { Normal, Magic, Rare, Unique, Gem, Currency, DivinationCard, Other }

/// <summary>Where a mod line came from, mirroring the trade site's stat groups.</summary>
public enum ModKind { Explicit, Implicit, Crafted, Fractured, Enchant, Scourge, Crucible, Unknown }

/// <summary>One mod line (or a multi-line mod joined with '\n') as it appears on the item.</summary>
public sealed class ItemMod
{
    public required string Text { get; init; }
    public ModKind Kind { get; init; }
    /// <summary>Numbers in the text, in order (range annotations from advanced copy removed).</summary>
    public IReadOnlyList<double> Values { get; init; } = [];
    /// <summary>Tier from advanced copy (1 = best), when known.</summary>
    public int? Tier { get; init; }
    /// <summary>"Prefix" / "Suffix" / null, from advanced copy.</summary>
    public string? Affix { get; init; }
    /// <summary>Mod tags from advanced copy, e.g. "Life", "Elemental, Fire, Resistance".</summary>
    public string? Tags { get; init; }
    /// <summary>Lines from the same advanced-copy header share a group (hybrid or multi-line mods).</summary>
    public int Group { get; init; }
    /// <summary>The first mod line exactly as shown in-game (without the advanced header).</summary>
    public string DisplayText => Text.Replace('\n', ' ');

    /// <summary>A single representative number: the average for "Adds # to #", else the first value.</summary>
    public double Value => Values.Count switch { 0 => 0, 2 when Text.StartsWith("Adds ") => (Values[0] + Values[1]) / 2, _ => Values[0] };
}

public sealed class ItemRequirements
{
    public int Level { get; set; }
    public int Str { get; set; }
    public int Dex { get; set; }
    public int Int { get; set; }
}

public sealed class ParsedItem
{
    public string RawText { get; init; } = "";
    public string ItemClass { get; set; } = "";
    public ItemRarity Rarity { get; set; }
    /// <summary>Unique/rare name, gem name, or currency name. Null for normal/magic items.</summary>
    public string? Name { get; set; }
    /// <summary>Base type as shown (magic affixes stripped once resolved against game data).</summary>
    public string BaseType { get; set; } = "";
    /// <summary>The magic/normal item header line before base-type resolution.</summary>
    public string? TypeLine { get; set; }

    public int Quality { get; set; }
    public int ItemLevel { get; set; }
    public int GemLevel { get; set; }
    public int StackSize { get; set; } = 1;
    public int MapTier { get; set; }
    public bool Corrupted { get; set; }
    public bool Mirrored { get; set; }
    public bool Identified { get; set; } = true;
    public bool Fractured { get; set; }
    public bool Synthesised { get; set; }
    public bool Split { get; set; }
    public bool Transfigured { get; set; }
    public bool AdvancedText { get; set; }
    public List<string> Influences { get; } = [];

    public string? Sockets { get; set; }
    public int SocketCount { get; set; }
    public int MaxLinks { get; set; }

    public ItemRequirements Requirements { get; } = new();
    /// <summary>Raw "Key: value" property lines from the property block (Armour, Physical Damage, ...).</summary>
    public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ItemMod> Mods { get; } = [];
    public string? Note { get; set; }

    public bool IsWeapon => ItemCategories.IsWeapon(ItemClass);
    public bool IsArmour => ItemCategories.IsArmour(ItemClass);
    public bool IsAccessory => ItemCategories.IsAccessory(ItemClass);
    public bool IsJewel => ItemClass.Contains("Jewel", StringComparison.OrdinalIgnoreCase);
    public bool IsFlask => ItemClass.EndsWith("Flasks", StringComparison.OrdinalIgnoreCase);
    public bool IsMap => ItemClass == "Maps";

    public string DisplayName => Name is null ? BaseType : (Rarity is ItemRarity.Rare or ItemRarity.Unique ? $"{Name} {BaseType}" : Name);
}
