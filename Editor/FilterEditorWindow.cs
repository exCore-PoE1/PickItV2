using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using ExileCore;
using ImGuiNET;
using ItemFilterLibrary;
using static PickIt.PickIt;

namespace PickIt.Editor;

// The GUI filter editor. A single window (one open at a time) with two columns: a rows editor on
// the left and a grab pane on the right. Edits are auto-saved on a debounce, but only after the
// document passes a compile gate (ItemFilter.LoadFromList) so a malformed row never overwrites a
// good file. Task 9 wires Tick/Render into PickIt's lifecycle and the rules table; this type only
// has to compile and behave self-contained.
public static class FilterEditorWindow
{
    private static FilterDocument _doc;
    private static string _path;
    private static bool _open;
    private static int _selectedRowIndex = -1;
    private static readonly ItemScanner _scanner = new();
    private static string _lastSerialized;
    private static readonly Stopwatch _sinceEdit = new();
    private static bool _dirty;
    private static string _saveStatus = "";

    // Header file-switch combo cache (rebuilt each time the window opens to a new file).
    private static string[] _fileChoices = [];
    private static string[] _fileChoiceFullPaths = [];

    public static bool IsOpen => _open;

    // ---- Open / lifecycle --------------------------------------------------------------------------

    public static void Open(string fileFullPath)
    {
        var text = ReadWithRetry(fileFullPath);
        if (text == null)
        {
            _saveStatus = "error: could not read file";
            return;
        }

        _doc = FilterDocument.Parse(text, fileFullPath);
        _path = fileFullPath;
        _open = true;
        _lastSerialized = _doc.Serialize();
        _dirty = false;
        _sinceEdit.Reset();
        _saveStatus = "";

        // Select the first ConditionRow, or -1 if none.
        _selectedRowIndex = -1;
        for (var i = 0; i < _doc.Entries.Count; i++)
            if (_doc.Entries[i] is ConditionRow) { _selectedRowIndex = i; break; }

        RefreshFileChoices();
    }

    public static void Tick(GameController gc)
    {
        if (!_open) return;
        _scanner.HandleGrabHotkey(gc, Main.Settings.GrabItemKey.Value);
        _scanner.RefreshPool(gc);
    }

    internal static void MarkDirty()
    {
        _dirty = true;
        _sinceEdit.Restart();
    }

    // ---- Render ------------------------------------------------------------------------------------

    public static void Render(GameController gc)
    {
        if (!_open) return;

        EditorTheme.Push();
        try
        {
            ImGui.SetNextWindowSize(new Vector2(900, 600), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Filter Editor##PickItFilterEditor", ref _open))
            {
                DrawHeader();
                ImGui.Separator();

                ImGui.Columns(2, "pickit_editor_cols", true);

                var headerHeight = ImGui.GetTextLineHeightWithSpacing();
                var bodyHeight = ImGui.GetContentRegionAvail().Y - headerHeight;
                if (bodyHeight < 150f) bodyHeight = 150f;
                var paneSize = new Vector2(0, bodyHeight);

                ImGui.TextDisabled("ROWS");
                if (ImGui.BeginChild("col_rows", paneSize, ImGuiChildFlags.None))
                    DrawRows();
                ImGui.EndChild();
                ImGui.NextColumn();

                ImGui.TextDisabled("GRAB");
                if (ImGui.BeginChild("col_grab", paneSize, ImGuiChildFlags.None))
                    DrawGrabPane();
                ImGui.EndChild();

                ImGui.Columns(1);

                // Debounced save: only fire after edits have settled.
                if (_dirty && _sinceEdit.ElapsedMilliseconds >= 750)
                    SaveAndApply();
            }

            ImGui.End();
        }
        finally
        {
            EditorTheme.Pop();
        }
    }

    private static void DrawHeader()
    {
        var fileName = string.IsNullOrEmpty(_path) ? "(none)" : Path.GetFileName(_path);
        ImGui.TextColored(Controls.Accent, fileName);
        ImGui.SameLine();

        // File-switch combo: pick another *.ifl under the config dir to edit.
        ImGui.SetNextItemWidth(220);
        var current = -1;
        for (var i = 0; i < _fileChoiceFullPaths.Length; i++)
            if (string.Equals(_fileChoiceFullPaths[i], _path, StringComparison.OrdinalIgnoreCase)) { current = i; break; }

        var pick = current;
        if (_fileChoices.Length > 0 && ImGui.Combo("##fileswitch", ref pick, _fileChoices, _fileChoices.Length))
        {
            if (pick >= 0 && pick < _fileChoiceFullPaths.Length &&
                !string.Equals(_fileChoiceFullPaths[pick], _path, StringComparison.OrdinalIgnoreCase))
            {
                // Flush any pending edits to the current file before switching away.
                if (_dirty) SaveAndApply();
                Open(_fileChoiceFullPaths[pick]);
            }
        }

        if (!string.IsNullOrEmpty(_saveStatus))
        {
            ImGui.SameLine();
            var col = _saveStatus.StartsWith("error", StringComparison.OrdinalIgnoreCase) ? Controls.Err : Controls.Ok;
            ImGui.TextColored(col, _saveStatus);
        }
    }

    // ---- Rows column -------------------------------------------------------------------------------

    private static void DrawRows()
    {
        if (_doc == null) return;

        // Deferred structural mutations so we never modify Entries while iterating it.
        var deleteRows = new List<int>();
        var duplicateRows = new List<int>();

        for (var i = 0; i < _doc.Entries.Count; i++)
        {
            ImGui.PushID(i);
            switch (_doc.Entries[i])
            {
                case Banner b:
                    ImGui.TextColored(Controls.Muted, b.Text);
                    break;

                case Blank:
                    ImGui.Dummy(new Vector2(1, 4));
                    break;

                case ConditionRow r:
                    DrawConditionRow(i, r, out var del, out var dup);
                    if (del) deleteRows.Add(i);
                    if (dup) duplicateRows.Add(i);
                    break;
            }
            ImGui.PopID();
        }

        // Apply deferred row deletions/duplications (highest index first so earlier indices stay valid).
        if (duplicateRows.Count > 0)
        {
            foreach (var idx in duplicateRows.OrderByDescending(x => x))
            {
                if (_doc.Entries[idx] is ConditionRow src)
                    _doc.Entries.Insert(idx + 1, CloneRow(src));
            }
            MarkDirty();
        }
        if (deleteRows.Count > 0)
        {
            foreach (var idx in deleteRows.OrderByDescending(x => x))
                _doc.Entries.RemoveAt(idx);
            if (_selectedRowIndex >= _doc.Entries.Count) _selectedRowIndex = -1;
            MarkDirty();
        }

        ImGui.Separator();
        if (ImGui.Button("+ add row"))
        {
            var row = new ConditionRow();          // empty: And = []
            _doc.Entries.Add(row);
            _selectedRowIndex = _doc.Entries.Count - 1;
            MarkDirty();
        }
        ImGui.SameLine();
        if (ImGui.Button("+ raw row"))
        {
            var row = new ConditionRow { And = [new Condition { Raw = "" }] };
            _doc.Entries.Add(row);
            _selectedRowIndex = _doc.Entries.Count - 1;
            MarkDirty();
        }
    }

    private static void DrawConditionRow(int rowIndex, ConditionRow r, out bool delete, out bool duplicate)
    {
        delete = false;
        duplicate = false;

        // Enable checkbox = comment/uncomment.
        var enabled = r.Enabled;
        if (ImGui.Checkbox("##enabled", ref enabled))
        {
            r.Enabled = enabled;
            MarkDirty();
        }
        ImGui.SameLine();

        // Per-row status dot: green if the compiled row is a valid ItemQuery.
        var compiled = QueryCompiler.CompileRow(r.And);
        var rowOk = string.IsNullOrWhiteSpace(compiled) ? (bool?)null : QueryValidator.Validate(compiled).ok;
        Controls.StatusDot(rowOk);
        ImGui.SameLine();

        // Selectable highlight tracking _selectedRowIndex.
        var selected = _selectedRowIndex == rowIndex;
        if (selected) ImGui.PushStyleColor(ImGuiCol.Text, Controls.Accent);
        var label = $"Row {rowIndex + 1}";
        if (ImGui.Selectable(label, selected, ImGuiSelectableFlags.None, new Vector2(70, 0)))
            _selectedRowIndex = rowIndex;
        if (selected) ImGui.PopStyleColor();

        ImGui.SameLine();
        if (ImGui.SmallButton("dup")) duplicate = true;
        ImGui.SameLine();
        if (ImGui.SmallButton("x##delrow")) delete = true;

        // Per-condition editors. Collect deferred mutations so we don't touch r.And mid-iteration.
        var removeConditions = new List<int>();
        var replacements = new List<(int index, Condition cond)>();

        ImGui.Indent();
        for (var ci = 0; ci < r.And.Count; ci++)
        {
            ImGui.PushID(ci);
            var c = r.And[ci];
            var replaced = DrawCondition(c, out var remove);
            if (remove) removeConditions.Add(ci);
            else if (replaced != null) replacements.Add((ci, replaced));
            ImGui.PopID();
        }

        if (ImGui.SmallButton("+ cond"))
        {
            r.And.Add(new Condition { Attribute = AttributeCatalog.All[0].Key, Operator = AttributeCatalog.All[0].Operators[0] });
            MarkDirty();
        }
        ImGui.Unindent();

        // Apply deferred condition mutations.
        foreach (var (index, cond) in replacements)
            if (index >= 0 && index < r.And.Count) r.And[index] = cond;
        if (removeConditions.Count > 0)
        {
            foreach (var idx in removeConditions.OrderByDescending(x => x))
                r.And.RemoveAt(idx);
            MarkDirty();
        }
        else if (replacements.Count > 0)
        {
            MarkDirty();
        }
    }

    // Renders one condition's chip editor. Returns a replacement Condition if it should be swapped in
    // (used by "try parse"); sets remove=true if the condition's delete button was pressed. Field-bound
    // ImGui controls mutate the Condition's fields in place and MarkDirty directly.
    private static Condition DrawCondition(Condition c, out bool remove)
    {
        remove = false;
        Condition replacement = null;

        if (c.Raw != null)
        {
            // Raw fragment: status dot + free-text edit + a "try parse" button.
            Controls.StatusDot(QueryValidator.Validate(c.Raw).ok);
            ImGui.SameLine();

            var raw = c.Raw;
            ImGui.SetNextItemWidth(240);
            if (ImGui.InputText("##raw", ref raw, 2000))
            {
                c.Raw = raw;
                MarkDirty();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("try parse"))
            {
                var parsed = QueryParser.ParseLeaf(c.Raw ?? "");
                if (parsed != null && parsed.Raw == null)
                {
                    replacement = parsed;   // recognized -> swap to a structured condition
                    MarkDirty();
                }
            }
        }
        else
        {
            // Structured attribute condition: attribute combo, operator combo, value-by-kind.
            var def = AttributeCatalog.Find(c.Attribute) ?? AttributeCatalog.All[0];

            // Attribute combo.
            var attrKeys = AttributeCatalog.All.Select(a => a.Key).ToArray();
            var attrIndex = Array.IndexOf(attrKeys, c.Attribute);
            if (attrIndex < 0) attrIndex = 0;
            var attrPick = attrIndex;
            ImGui.PushStyleColor(ImGuiCol.Text, Controls.AttrColor(def.Kind));
            ImGui.SetNextItemWidth(130);
            var attrChanged = ImGui.Combo("##attr", ref attrPick, attrKeys, attrKeys.Length);
            ImGui.PopStyleColor();
            if (attrChanged && attrPick != attrIndex)
            {
                c.Attribute = attrKeys[attrPick];
                var newDef = AttributeCatalog.Find(c.Attribute);
                c.Operator = newDef?.Operators is { Length: > 0 } ops ? ops[0] : c.Operator;
                c.Value = null;   // value form changed; clear stale value
                def = newDef ?? def;
                MarkDirty();
            }

            // Operator combo.
            ImGui.SameLine();
            var opList = def.Operators ?? [];
            if (opList.Length > 0)
            {
                var opIndex = Array.IndexOf(opList, c.Operator);
                if (opIndex < 0) opIndex = 0;
                var opPick = opIndex;
                ImGui.SetNextItemWidth(90);
                if (ImGui.Combo("##op", ref opPick, opList, opList.Length) && opPick != opIndex)
                {
                    c.Operator = opList[opPick];
                    MarkDirty();
                }
            }

            // Value input by kind.
            switch (def.Kind)
            {
                case AttrKind.Bool:
                    // No value control (operator carries has/notHas).
                    break;

                case AttrKind.Enum:
                    ImGui.SameLine();
                    var enumValues = def.EnumValues ?? [];
                    if (enumValues.Length > 0)
                    {
                        var enumIndex = Array.IndexOf(enumValues, c.Value);
                        if (enumIndex < 0) enumIndex = 0;
                        var enumPick = enumIndex;
                        ImGui.SetNextItemWidth(110);
                        if (ImGui.Combo("##val", ref enumPick, enumValues, enumValues.Length))
                        {
                            c.Value = enumValues[enumPick];
                            MarkDirty();
                        }
                        else if (c.Value == null)
                        {
                            c.Value = enumValues[enumIndex];   // seed a value so the row compiles
                        }
                    }
                    break;

                default: // Number / String
                    ImGui.SameLine();
                    var val = c.Value ?? "";
                    ImGui.SetNextItemWidth(130);
                    if (ImGui.InputText("##val", ref val, 200))
                    {
                        c.Value = val;
                        MarkDirty();
                    }
                    break;
            }
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("x##delcond")) remove = true;

        return replacement;
    }

    // ---- Grab column -------------------------------------------------------------------------------

    private static void DrawGrabPane()
    {
        var grabKey = Main.Settings.GrabItemKey.Value;
        ImGui.TextWrapped($"Hover an item, press [{grabKey}] to grab it.");
        if (ImGui.SmallButton($"Clear ({_scanner.Pool.Count})"))
            _scanner.Clear();

        ImGui.Separator();

        if (_scanner.Pool.Count == 0)
        {
            ImGui.TextDisabled("Pool is empty. Hover an item in-game and press the grab key.");
            return;
        }

        // Pending removals applied after the loop, never mid-iteration over Pool.
        var removeIndex = -1;
        for (var i = 0; i < _scanner.Pool.Count; i++)
        {
            ImGui.PushID(i);
            DrawGrabbedCard(_scanner.Pool[i], out var remove);
            ImGui.PopID();
            if (remove) removeIndex = i;
        }
        if (removeIndex >= 0) _scanner.Pool.RemoveAt(removeIndex);
    }

    private static void DrawGrabbedCard(GrabbedItem item, out bool remove)
    {
        remove = false;
        var chips = item.Chips ?? [];

        var className = chips.FirstOrDefault(c => c.Category == ChipCategory.Class)?.Display;
        var baseName = chips.FirstOrDefault(c => c.Category == ChipCategory.Base)?.Display;
        var header = !string.IsNullOrEmpty(baseName) && !string.IsNullOrEmpty(className)
            ? $"{baseName}  -  {className}"
            : (string.IsNullOrEmpty(baseName) ? className : baseName);

        ImGui.BeginChild("card", new Vector2(0, 0), ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY);

        ImGui.PushStyleColor(ImGuiCol.Text, Controls.Accent);
        ImGui.TextUnformatted(string.IsNullOrEmpty(header) ? "(item)" : header);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGui.SmallButton("x")) remove = true;

        if (chips.Count == 0)
        {
            ImGui.TextDisabled("loading item data... (pick it up / move closer in-game)");
            ImGui.EndChild();
            return;
        }

        // Non-mod chips: one button each.
        foreach (var c in chips.Where(c => c.Category != ChipCategory.Mod))
        {
            ImGui.PushID(c.DedupKey);
            if (ImGui.SmallButton(c.Display ?? "(chip)"))
                AddChipToSelected(c);
            ImGui.PopID();
        }

        // Mod chips: button + threshold slider bound to Mod.Threshold.
        var mods = chips.Where(c => c.Category == ChipCategory.Mod).ToList();
        if (mods.Count > 0) ImGui.TextDisabled("mods");
        foreach (var c in mods)
        {
            ImGui.PushID(c.DedupKey);
            if (ImGui.SmallButton(c.Display ?? "(mod)"))
                AddChipToSelected(c);

            if (c.Mod != null)
            {
                var min = c.Mod.Min ?? 0;
                var max = c.Mod.Max ?? (c.Mod.Threshold * 2);
                if (max <= min) max = min + 1;   // ImGui requires a non-empty range
                var threshold = c.Mod.Threshold;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderInt("##thr", ref threshold, min, max))
                    c.Mod.Threshold = threshold;
            }
            ImGui.PopID();
        }

        ImGui.EndChild();
    }

    // Append a chip's condition to the selected ConditionRow, creating one if nothing is selected.
    private static void AddChipToSelected(Chip chip)
    {
        if (_doc == null) return;

        ConditionRow target = null;
        if (_selectedRowIndex >= 0 && _selectedRowIndex < _doc.Entries.Count &&
            _doc.Entries[_selectedRowIndex] is ConditionRow sel)
        {
            target = sel;
        }
        else
        {
            target = new ConditionRow { And = [] };
            _doc.Entries.Add(target);
            _selectedRowIndex = _doc.Entries.Count - 1;
        }

        target.And.Add(chip.ToCondition());
        MarkDirty();
    }

    // ---- Save / gate -------------------------------------------------------------------------------

    private static void SaveAndApply()
    {
        if (_doc == null) { _dirty = false; return; }

        var serialized = _doc.Serialize();
        if (serialized == _lastSerialized)
        {
            _dirty = false;
            return;
        }

        // GATE: compile every enabled row through the same loader the engine uses. A malformed line
        // throws here and we refuse to write — the good file on disk is left untouched.
        try
        {
            ItemFilter.LoadFromList("editor-preview", _doc.EnabledRowLines().ToList());
        }
        catch (Exception ex)
        {
            _saveStatus = "error: " + ex.Message;
            _dirty = false;
            return;
        }

        if (!WriteWithRetry(_path, serialized))
        {
            _saveStatus = "error: could not write file";
            _dirty = false;
            return;
        }

        _lastSerialized = serialized;
        _dirty = false;
        _saveStatus = "saved";
        RulesDisplay.LoadAndApplyRules();
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static ConditionRow CloneRow(ConditionRow src) => new()
    {
        Enabled = src.Enabled,
        And = src.And.Select(CloneCondition).ToList(),
    };

    private static Condition CloneCondition(Condition c) => new()
    {
        Attribute = c.Attribute,
        Operator = c.Operator,
        Value = c.Value,
        Raw = c.Raw,
        Mod = c.Mod == null ? null : new ModRef
        {
            Display = c.Mod.Display,
            MatchToken = c.Mod.MatchToken,
            GameStats = c.Mod.GameStats,
            MatchByStat = c.Mod.MatchByStat,
            Mode = c.Mod.Mode,
            ValueIndex = c.Mod.ValueIndex,
            Threshold = c.Mod.Threshold,
            Min = c.Mod.Min,
            Max = c.Mod.Max,
        },
    };

    private static void RefreshFileChoices()
    {
        try
        {
            var dir = RulesDisplay.GetPickitConfigFileDirectory();
            var files = new DirectoryInfo(dir)
                .GetFiles("*.ifl", SearchOption.AllDirectories)
                .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _fileChoiceFullPaths = files.Select(f => f.FullName).ToArray();
            _fileChoices = files.Select(f => Path.GetRelativePath(dir, f.FullName).Replace("\\", "/")).ToArray();
        }
        catch
        {
            _fileChoices = [];
            _fileChoiceFullPaths = [];
        }
    }

    private static string ReadWithRetry(string path)
    {
        const int maxRetries = 10;
        for (var attempt = 0; ; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException)
            {
                if (attempt >= maxRetries - 1) return null;
                System.Threading.Thread.Sleep(100);
            }
            catch { return null; }
        }
    }

    private static bool WriteWithRetry(string path, string content)
    {
        const int maxRetries = 10;
        for (var attempt = 0; ; attempt++)
        {
            try { File.WriteAllText(path, content); return true; }
            catch (IOException)
            {
                if (attempt >= maxRetries - 1) return false;
                System.Threading.Thread.Sleep(100);
            }
            catch { return false; }
        }
    }
}

