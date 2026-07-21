using System.Numerics;
using ImGuiNET;

namespace PickIt.Editor;

public static class EditorTheme
{
    private static int _colors;
    private static int _vars;

    public static void Push()
    {
        void C(ImGuiCol id, Vector4 v) { ImGui.PushStyleColor(id, v); _colors++; }
        void V(ImGuiStyleVar id, Vector2 v) { ImGui.PushStyleVar(id, v); _vars++; }
        void V1(ImGuiStyleVar id, float v) { ImGui.PushStyleVar(id, v); _vars++; }

        _colors = 0; _vars = 0;
        C(ImGuiCol.Header,    new Vector4(0.30f, 0.20f, 0.08f, 1f));
        C(ImGuiCol.Button,    new Vector4(0.35f, 0.22f, 0.08f, 1f));
        C(ImGuiCol.FrameBg,   new Vector4(0.18f, 0.14f, 0.10f, 1f));
        C(ImGuiCol.SliderGrab, Controls.Accent);
        C(ImGuiCol.CheckMark,  Controls.Accent);
        V1(ImGuiStyleVar.FrameRounding, 4f);
        V(ImGuiStyleVar.ItemSpacing, new Vector2(8, 6));
        V1(ImGuiStyleVar.IndentSpacing, 14f);
    }

    public static void Pop()
    {
        ImGui.PopStyleVar(_vars);
        ImGui.PopStyleColor(_colors);
        _vars = 0; _colors = 0;
    }
}

