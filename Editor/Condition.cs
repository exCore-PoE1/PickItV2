using System.Collections.Generic;

namespace PickIt.Editor;

public enum AttrKind { String, Number, Bool, Enum }

public class AttrDef
{
    public string Key;          // user-facing + Condition.Attribute
    public string Dsl;          // DSL property, e.g. "ClassName"
    public AttrKind Kind;
    public string EnumType;     // for Enum kind, e.g. "ItemRarity"
    public string[] EnumValues; // for Enum kind
    public string[] Operators;  // operators offered in the UI
}

// A single leaf. Exactly one of {Attribute, Mod, Raw} is the active form:
//   Attribute != null -> structured attribute condition
//   Mod != null       -> mod condition
//   Raw != null       -> verbatim DSL fragment (lossless fallback)
public class Condition
{
    public string Attribute;
    public string Operator;     // "==","!=",">=","<=",">","<","contains","startsWith","endsWith","has","notHas"
    public string Value;
    public string Raw;
    public ModRef Mod;
}

public class ModRef
{
    public string Display;
    public string MatchToken;
    public string[] GameStats = [];
    public bool MatchByStat;
    public string Mode = "Value";   // "Has" | "Value"
    public int ValueIndex;
    public int Threshold;
    public int? Min, Max;
}

public static partial class AttributeCatalog
{
    // String attrs include startsWith/endsWith (PickIt commonly uses ClassName.EndsWith).
    public static readonly List<AttrDef> All =
    [
        new() { Key = "ClassName", Dsl = "ClassName", Kind = AttrKind.String, Operators = ["==","!=","contains","startsWith","endsWith"] },
        new() { Key = "BaseName",  Dsl = "BaseName",  Kind = AttrKind.String, Operators = ["==","!=","contains","startsWith","endsWith"] },
        new() { Key = "Path",      Dsl = "Path",      Kind = AttrKind.String, Operators = ["contains","startsWith","endsWith"] },
        new() { Key = "Rarity",    Dsl = "Rarity",    Kind = AttrKind.Enum,   EnumType = "ItemRarity",
                EnumValues = ["Normal","Magic","Rare","Unique"], Operators = ["==","!="] },
        new() { Key = "ItemLevel",   Dsl = "ItemLevel",   Kind = AttrKind.Number, Operators = ["==","!=",">=","<=",">","<"] },
        new() { Key = "ItemQuality", Dsl = "ItemQuality", Kind = AttrKind.Number, Operators = ["==",">=","<="] },
        new() { Key = "Armour",       Dsl = "ArmourInfo.Armour",  Kind = AttrKind.Number, Operators = ["==",">=","<="] },
        new() { Key = "Evasion",      Dsl = "ArmourInfo.Evasion", Kind = AttrKind.Number, Operators = ["==",">=","<="] },
        new() { Key = "EnergyShield", Dsl = "ArmourInfo.ES",      Kind = AttrKind.Number, Operators = ["==",">=","<="] },
        new() { Key = "MapTier",     Dsl = "MapInfo.Tier",     Kind = AttrKind.Number, Operators = ["==",">=","<="] },
        new() { Key = "Sockets",     Dsl = "SocketInfo.SocketNumber",    Kind = AttrKind.Number, Operators = ["==",">="] },
        new() { Key = "Links",       Dsl = "SocketInfo.LargestLinkSize", Kind = AttrKind.Number, Operators = ["==",">="] },
        new() { Key = "StackSize",   Dsl = "StackInfo.Count",  Kind = AttrKind.Number, Operators = ["==",">=","<="] },
        new() { Key = "VeiledModCount", Dsl = "VeiledModCount", Kind = AttrKind.Number, Operators = ["==",">=","<="] },

        new() { Key = "IsCorrupted",  Dsl = "IsCorrupted",  Kind = AttrKind.Bool, Operators = ["has","notHas"] },
        new() { Key = "IsIdentified", Dsl = "IsIdentified", Kind = AttrKind.Bool, Operators = ["has","notHas"] },
        new() { Key = "IsWeapon",     Dsl = "IsWeapon",     Kind = AttrKind.Bool, Operators = ["has","notHas"] },
        new() { Key = "Enchanted",    Dsl = "Enchanted",    Kind = AttrKind.Bool, Operators = ["has","notHas"] },
    ];

    public static AttrDef Find(string key)
    {
        foreach (var d in All) if (d.Key == key) return d;
        return null;
    }

    public static AttrDef FindByDsl(string dsl)
    {
        foreach (var d in All) if (d.Dsl == dsl) return d;
        return null;
    }
}

