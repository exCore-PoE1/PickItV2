using System.Numerics;
using ImGuiNET;

namespace PickIt.Editor;

public static class Controls
{
    public static readonly Vector4 Accent = new(0.95f, 0.60f, 0.20f, 1f);
    public static readonly Vector4 Warn   = new(0.95f, 0.75f, 0.35f, 1f);
    public static readonly Vector4 Muted  = new(0.6f, 0.6f, 0.6f, 1f);
    public static readonly Vector4 Ok     = new(0.40f, 0.85f, 0.40f, 1f);
    public static readonly Vector4 Err    = new(0.90f, 0.35f, 0.35f, 1f);

    // ok == true -> green dot; false -> red dot; null -> grey dot.
    public static void StatusDot(bool? ok)
    {
        var (col, glyph) = ok switch
        {
            true  => (Ok, "(*)"),
            false => (Err, "(x)"),
            _     => (Muted, "( )"),
        };
        ImGui.TextColored(col, glyph);
    }

    public static Vector4 AttrColor(AttrKind kind) => kind switch
    {
        AttrKind.String => new Vector4(0.85f, 0.73f, 0.40f, 1f),
        AttrKind.Number => new Vector4(0.40f, 0.70f, 0.90f, 1f),
        AttrKind.Enum   => new Vector4(0.70f, 0.55f, 0.90f, 1f),
        AttrKind.Bool   => new Vector4(0.95f, 0.55f, 0.50f, 1f),
        _               => Muted,
    };
}

