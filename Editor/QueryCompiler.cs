using System.Collections.Generic;
using System.Linq;

namespace PickIt.Editor;

public static class QueryCompiler
{
    public static string CompileRow(List<Condition> row)
    {
        if (row == null) return "";
        var parts = row.Select(CompileLeaf).Where(s => !string.IsNullOrEmpty(s));
        return string.Join(" && ", parts);
    }

    public static string CompileLeaf(Condition n)
    {
        if (n == null) return "";
        if (!string.IsNullOrEmpty(n.Raw)) return n.Raw;
        if (n.Mod != null) return CompileMod(n.Mod);

        var def = AttributeCatalog.Find(n.Attribute);
        if (def == null) return "";

        switch (def.Kind)
        {
            case AttrKind.String:
                return n.Operator switch
                {
                    "contains"   => $"{def.Dsl}.Contains(\"{Escape(n.Value)}\")",
                    "startsWith" => $"{def.Dsl}.StartsWith(\"{Escape(n.Value)}\")",
                    "endsWith"   => $"{def.Dsl}.EndsWith(\"{Escape(n.Value)}\")",
                    _            => $"{def.Dsl} {n.Operator} \"{Escape(n.Value)}\"",
                };
            case AttrKind.Number: return $"{def.Dsl} {n.Operator} {n.Value}";
            case AttrKind.Enum:   return $"{def.Dsl} {n.Operator} {def.EnumType}.{n.Value}";
            case AttrKind.Bool:   return n.Operator == "notHas" ? $"!{def.Dsl}" : def.Dsl;
            default: return "";
        }
    }

    private static string CompileMod(ModRef m)
    {
        if (m.MatchByStat)
        {
            var stat = m.GameStats is { Length: > 0 } ? m.GameStats[0] : "";
            return $"ItemStats[GameStat.{stat}] >= {m.Threshold}";
        }
        if (m.Mode == "Has") return $"FindMods(\"{Escape(m.MatchToken)}\").Any()";
        return $"FindMods(\"{Escape(m.MatchToken)}\").Any(Values[{m.ValueIndex}] >= {m.Threshold})";
    }

    private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
}

