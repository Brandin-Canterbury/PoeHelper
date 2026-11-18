namespace PoeHelper.Core.Items;

/// <summary>Maps the in-game "Item Class:" line to trade-site categories (from /api/trade/data/filters).</summary>
public static class ItemCategories
{
    static readonly Dictionary<string, string> TradeCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Body Armours"] = "armour.chest",
        ["Helmets"] = "armour.helmet",
        ["Gloves"] = "armour.gloves",
        ["Boots"] = "armour.boots",
        ["Shields"] = "armour.shield",
        ["Quivers"] = "armour.quiver",
        ["Amulets"] = "accessory.amulet",
        ["Rings"] = "accessory.ring",
        ["Belts"] = "accessory.belt",
        ["Trinkets"] = "accessory.trinket",
        ["Jewels"] = "jewel.base",
        ["Abyss Jewels"] = "jewel.abyss",
        ["Bows"] = "weapon.bow",
        ["Claws"] = "weapon.claw",
        ["Daggers"] = "weapon.basedagger",
        ["Rune Daggers"] = "weapon.runedagger",
        ["One Hand Axes"] = "weapon.oneaxe",
        ["One Hand Swords"] = "weapon.basesword",
        ["Thrusting One Hand Swords"] = "weapon.rapier",
        ["One Hand Maces"] = "weapon.basemace",
        ["Sceptres"] = "weapon.sceptre",
        ["Staves"] = "weapon.basestaff",
        ["Warstaves"] = "weapon.warstaff",
        ["Two Hand Axes"] = "weapon.twoaxe",
        ["Two Hand Maces"] = "weapon.twomace",
        ["Two Hand Swords"] = "weapon.twosword",
        ["Wands"] = "weapon.wand",
        ["Fishing Rods"] = "weapon.rod",
        ["Maps"] = "map",
        ["Divination Cards"] = "card",
        ["Tinctures"] = "tincture",
    };

    static readonly HashSet<string> Weapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bows", "Claws", "Daggers", "Rune Daggers", "One Hand Axes", "One Hand Swords", "Thrusting One Hand Swords",
        "One Hand Maces", "Sceptres", "Staves", "Warstaves", "Two Hand Axes", "Two Hand Maces", "Two Hand Swords",
        "Wands", "Fishing Rods",
    };

    static readonly HashSet<string> Armours = new(StringComparer.OrdinalIgnoreCase)
    { "Body Armours", "Helmets", "Gloves", "Boots", "Shields", "Quivers" };

    static readonly HashSet<string> Accessories = new(StringComparer.OrdinalIgnoreCase)
    { "Amulets", "Rings", "Belts", "Trinkets" };

    public static string? ToTradeCategory(string itemClass)
    {
        if (TradeCategory.TryGetValue(itemClass, out var c)) return c;
        if (itemClass.EndsWith("Flasks", StringComparison.OrdinalIgnoreCase)) return "flask";
        if (itemClass.Contains("Cluster", StringComparison.OrdinalIgnoreCase)) return "jewel.cluster";
        return null;
    }

    public static bool IsWeapon(string itemClass) => Weapons.Contains(itemClass);
    public static bool IsArmour(string itemClass) => Armours.Contains(itemClass);
    public static bool IsAccessory(string itemClass) => Accessories.Contains(itemClass);
    public static bool IsTwoHanded(string itemClass) =>
        itemClass.StartsWith("Two Hand", StringComparison.OrdinalIgnoreCase) || itemClass is "Bows" or "Staves" or "Warstaves" or "Fishing Rods";
}
