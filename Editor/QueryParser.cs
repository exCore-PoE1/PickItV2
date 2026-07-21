using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PickIt.Editor;

// Reverses QueryCompiler at the leaf/row level. Recognized atoms become structured
// Conditions; anything else becomes a Raw Condition, so parse is total.
public static class QueryParser
{
    // A row is an AND of leaves. Top-level || can't be expressed as a flat AND,
    // so the whole line is preserved as one Raw condition (no auto-splitting).
    // Method-call parens (FindMods(...), Contains(...), etc.) are handled by
    // ParseLeaf's recognizers — they must NOT trigger a Raw fallback here.
    public static List<Condition> ParseRow(string line)
    {
        var s = (line ?? "").Trim();
        if (s.Length == 0) return [];
        if (SplitTopLevel(s, "||").Count > 1)
            return [new Condition { Raw = s }];
        var parts = SplitTopLevel(s, "&&");
        var list = new List<Condition>(parts.Count);
        foreach (var p in parts) list.Add(ParseLeaf(p));
        return list;
    }

    // ---- Ported verbatim from Stashie QueryParser.SplitTopLevel ----
    private static List<string> SplitTopLevel(string s, string op)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        bool inStr = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr) { if (c == '\\') { i++; continue; } if (c == '"') inStr = false; continue; }
            if (c == '"') { inStr = true; continue; }
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }
            if (depth == 0 && c == op[0] && i + 1 < s.Length && s[i + 1] == op[1])
            {
                parts.Add(s.Substring(start, i - start).Trim());
                i++; start = i + 1;
            }
        }
        parts.Add(s.Substring(start).Trim());
        return parts;
    }

    private static readonly Regex RxStat       = new(@"^ItemStats\[GameStat\.([A-Za-z0-9_]+)\]\s*>=\s*(-?\d+)$");
    private static readonly Regex RxStatLegacy = new(@"^ItemStats\.GetValueOrDefault\(GameStat\.([A-Za-z0-9_]+)\)\s*>=\s*(-?\d+)$");
    private static readonly Regex RxModVal     = new(@"^FindMods\(""((?:[^""\\]|\\.)*)""\)\.Any\(Values\[(\d+)\]\s*>=\s*(-?\d+)\)$");
    private static readonly Regex RxModHas     = new(@"^FindMods\(""((?:[^""\\]|\\.)*)""\)\.Any\(\)$");
    private static readonly Regex RxContains   = new(@"^([A-Za-z0-9_.]+)\.Contains\(""((?:[^""\\]|\\.)*)""\)$");
    private static readonly Regex RxStartsWith = new(@"^([A-Za-z0-9_.]+)\.StartsWith\(""((?:[^""\\]|\\.)*)""\)$");
    private static readonly Regex RxEndsWith   = new(@"^([A-Za-z0-9_.]+)\.EndsWith\(""((?:[^""\\]|\\.)*)""\)$");
    private static readonly Regex RxNeg        = new(@"^!\s*([A-Za-z0-9_.]+)$");
    private static readonly Regex RxBare       = new(@"^([A-Za-z0-9_.]+)$");
    private static readonly Regex RxBinary     = new(@"^([A-Za-z0-9_.]+)\s*(==|!=|>=|<=|>|<)\s*(.+)$");

    public static Condition ParseLeaf(string s)
    {
        s = (s ?? "").Trim();
        Condition n;
        if ((n = TryStat(s)) != null) return n;
        if ((n = TryModVal(s)) != null) return n;
        if ((n = TryModHas(s)) != null) return n;
        if ((n = TryStringCall(s, RxContains, "contains")) != null) return n;
        if ((n = TryStringCall(s, RxStartsWith, "startsWith")) != null) return n;
        if ((n = TryStringCall(s, RxEndsWith, "endsWith")) != null) return n;
        if ((n = TryBool(s)) != null) return n;
        if ((n = TryBinary(s)) != null) return n;
        return new Condition { Raw = s };
    }

    private static Condition TryStat(string s)
    {
        var m = RxStat.Match(s); if (!m.Success) m = RxStatLegacy.Match(s);
        if (!m.Success) return null;
        return new Condition { Mod = new ModRef {
            MatchByStat = true, Mode = "Value", GameStats = [m.Groups[1].Value],
            Threshold = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) } };
    }

    private static Condition TryModVal(string s)
    {
        var m = RxModVal.Match(s); if (!m.Success) return null;
        return new Condition { Mod = new ModRef {
            Mode = "Value", MatchToken = Unescape(m.Groups[1].Value),
            ValueIndex = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            Threshold = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) } };
    }

    private static Condition TryModHas(string s)
    {
        var m = RxModHas.Match(s); if (!m.Success) return null;
        return new Condition { Mod = new ModRef { Mode = "Has", MatchToken = Unescape(m.Groups[1].Value) } };
    }

    private static Condition TryStringCall(string s, Regex rx, string op)
    {
        var m = rx.Match(s); if (!m.Success) return null;
        var def = AttributeCatalog.FindByDsl(m.Groups[1].Value);
        if (def == null || def.Kind != AttrKind.String) return null;
        return new Condition { Attribute = def.Key, Operator = op, Value = Unescape(m.Groups[2].Value) };
    }

    private static Condition TryBool(string s)
    {
        var neg = RxNeg.Match(s);
        if (neg.Success)
        {
            var d = AttributeCatalog.FindByDsl(neg.Groups[1].Value);
            if (d != null && d.Kind == AttrKind.Bool) return new Condition { Attribute = d.Key, Operator = "notHas" };
            return null;
        }
        var bare = RxBare.Match(s);
        if (bare.Success)
        {
            var d = AttributeCatalog.FindByDsl(bare.Groups[1].Value);
            if (d != null && d.Kind == AttrKind.Bool) return new Condition { Attribute = d.Key, Operator = "has" };
        }
        return null;
    }

    private static Condition TryBinary(string s)
    {
        var m = RxBinary.Match(s); if (!m.Success) return null;
        var def = AttributeCatalog.FindByDsl(m.Groups[1].Value);
        if (def == null) return null;
        var op = m.Groups[2].Value;
        var rhs = m.Groups[3].Value.Trim();
        switch (def.Kind)
        {
            case AttrKind.String:
                if ((op == "==" || op == "!=") && rhs.Length >= 2 && rhs[0] == '"' && rhs[^1] == '"')
                    return new Condition { Attribute = def.Key, Operator = op, Value = Unescape(rhs.Substring(1, rhs.Length - 2)) };
                return null;
            case AttrKind.Enum:
                var em = Regex.Match(rhs, "^" + Regex.Escape(def.EnumType) + @"\.([A-Za-z0-9_]+)$");
                if (em.Success) return new Condition { Attribute = def.Key, Operator = op, Value = em.Groups[1].Value };
                return null;
            case AttrKind.Number:
                if (Regex.IsMatch(rhs, @"^-?\d+$")) return new Condition { Attribute = def.Key, Operator = op, Value = rhs };
                return null;
            default: return null;
        }
    }

    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}

