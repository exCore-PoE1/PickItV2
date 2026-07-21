using System.Collections.Generic;
using System.Linq;

namespace PickIt.Editor;

public abstract class Entry { }

public sealed class ConditionRow : Entry
{
    public bool Enabled = true;
    public List<Condition> And = [];
}

public sealed class Banner : Entry { public string Text = ""; }
public sealed class Blank : Entry { }

public sealed class FilterDocument
{
    public string FilePath;
    public List<Entry> Entries = [];

    public static FilterDocument Parse(string text, string path)
    {
        var doc = new FilterDocument { FilePath = path };
        text ??= "";
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) { doc.Entries.Add(new Blank()); continue; }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//"))
            {
                var remainder = trimmed.Substring(2).Trim();
                if (LooksLikeCondition(remainder))
                    doc.Entries.Add(new ConditionRow { Enabled = false, And = QueryParser.ParseRow(remainder) });
                else
                    doc.Entries.Add(new Banner { Text = line });
                continue;
            }

            doc.Entries.Add(new ConditionRow { Enabled = true, And = QueryParser.ParseRow(line) });
        }
        return doc;
    }

    // A commented line is a disabled row (not a prose banner) when its content parses
    // to at least one *recognized* (non-Raw) condition, or to a multi-leaf && chain.
    private static bool LooksLikeCondition(string remainder)
    {
        if (string.IsNullOrWhiteSpace(remainder)) return false;
        var parsed = QueryParser.ParseRow(remainder);
        if (parsed.Count == 0) return false;
        if (parsed.Count > 1) return true;                 // a && b ...
        return string.IsNullOrEmpty(parsed[0].Raw);        // single recognized leaf
    }

    public string Serialize()
    {
        var lines = new List<string>(Entries.Count);
        for (int i = 0; i < Entries.Count; i++)
        {
            switch (Entries[i])
            {
                case Blank:
                    lines.Add("");
                    break;
                case Banner b:
                    lines.Add(b.Text);
                    break;
                case ConditionRow r:
                    var body = QueryCompiler.CompileRow(r.And);
                    lines.Add(r.Enabled ? body : "// " + body);
                    // A query must be terminated by a blank line, else ItemFilterLibrary joins it
                    // with the following line into one (broken) query. Continuation lines (ending in
                    // || or &&) are part of the same query, so they must NOT be separated.
                    if (r.Enabled && !string.IsNullOrWhiteSpace(body) && !EndsWithContinuation(body))
                    {
                        var next = i + 1 < Entries.Count ? Entries[i + 1] : null;
                        if (next != null && next is not Blank)
                            lines.Add("");
                    }
                    break;
            }
        }
        return string.Join("\n", lines);
    }

    // Mirrors ItemFilterLibrary's comment stripping (naive IndexOf "//"), then checks for a
    // trailing boolean operator that continues the query onto the next physical line.
    private static bool EndsWithContinuation(string body)
    {
        var idx = body.IndexOf("//", System.StringComparison.Ordinal);
        var code = (idx >= 0 ? body.Substring(0, idx) : body).TrimEnd();
        return code.EndsWith("||", System.StringComparison.Ordinal)
            || code.EndsWith("&&", System.StringComparison.Ordinal);
    }

    public IEnumerable<string> EnabledRowLines() =>
        Entries.OfType<ConditionRow>()
               .Where(r => r.Enabled)
               .Select(r => QueryCompiler.CompileRow(r.And))
               .Where(s => !string.IsNullOrWhiteSpace(s));
}

