using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;                 // Keys
using ExileCore;                           // GameController, Input
using ExileCore.PoEMemory.Components;      // Mods
using ExileCore.PoEMemory.MemoryObjects;   // Entity, ItemMod
using ExileCore.Shared.Enums;              // ItemRarity
using ItemFilterLibrary;                    // ItemData

namespace PickIt.Editor;

public enum ChipCategory { Class, Base, Rarity, Stat, Flag, Socket, Mod }

// One clickable chip = one Condition the editor can add. Non-mod chips carry catalog
// key/operator/value directly; mod chips carry a ModRef. ToCondition() maps the chip to the
// editor's Condition form (see Condition.cs) with no further translation, because ChipsForItem
// already emits catalog-valid keys ("ClassName", "Rarity", ...) and operators ("==", ">=", "has").
public sealed class Chip
{
    public ChipCategory Category;
    public string Display;        // shown on the chip
    public string Attribute;      // catalog key for non-mod chips (e.g. "ClassName")
    public string Operator;       // default operator for non-mod chips
    public string Value;          // default value for non-mod chips
    public ModRef Mod;            // populated for mod chips
    public string DedupKey;       // category + identity

    public Condition ToCondition() =>
        Mod != null
            ? new Condition { Mod = Mod }
            : new Condition { Attribute = Attribute, Operator = Operator, Value = Value };
}

// A grabbed item is a value snapshot of plain Chip objects, NOT a live memory handle. The card
// renders Chips directly. While the source entity is still the same live item we re-read it fresh
// each tick and MERGE the result in monotonically (RefreshPool/Merge): mods that stream in after the
// grab get added, real rolls upgrade placeholders, but a good value is never downgraded and a mod is
// never dropped — so a teardown read during pickup can't reset values to 0 / garbage. Once the read
// settles (or the source entity is picked up / freed / its slot reused) the snapshot is frozen.
public sealed class GrabbedItem
{
    public List<Chip> Chips = [];   // rendered by the card; merged across captures, never downgraded
    public bool Frozen;             // true => snapshot is final, stop touching live memory

    internal Entity Source;         // live source entity, re-wrapped into a FRESH ItemData each capture
    internal long SrcAddress;       // source-entity identity, used to detect slot reuse
    internal uint SrcId;
    internal string SrcPath;
    internal int StableTicks;       // ticks since the snapshot last improved; drives the settle-freeze
    internal int AgeTicks;          // ticks since grab; drives the absolute CPU backstop
}

// Snapshots the HOVERED item (UIHover) into a session pool of frozen Chip snapshots, scoped to
// PickIt's AttributeCatalog. Self-contained: takes GameController as a parameter, holds no plugin
// or settings coupling.
public sealed class ItemScanner
{
    // Session-only pool of grabbed items (frozen Chip snapshots, not live memory handles).
    public readonly List<GrabbedItem> Pool = [];

    private bool _grabKeyWasDown;

    public void Clear() => Pool.Clear();

    // Edge-detected grab; call from Tick. Reads the hovered item via UIHover (mirrors PickIt's
    // FilterTest path) and adds a frozen-on-settle chip snapshot to the pool.
    public void HandleGrabHotkey(GameController gc, Keys grabKey)
    {
        var isDown = Input.IsKeyDown(grabKey);
        var pressed = isDown && !_grabKeyWasDown;
        _grabKeyWasDown = isDown;
        if (!pressed) return;
        if (gc == null) return;

        if (gc.IngameState?.UIHover is { Address: not 0 } h && h.Entity is { IsValid: true } ent)
            TryPool(gc, ent);
    }

    // Only pool entities that resolve to a real item. A non-item hover yields an ItemData whose lazy
    // stat aggregation throws when chips are read, which would otherwise blank the scanner — reject
    // it here.
    private void TryPool(GameController gc, Entity ent)
    {
        if (ent == null || ent.Address == 0) return;
        try
        {
            var data = new ItemData(ent, gc);
            if (string.IsNullOrEmpty(data.Path) || !data.Path.Contains("Metadata/Items")) return;

            var g = new GrabbedItem
            {
                Source = ent,
                SrcAddress = ent.Address,
                SrcId = ent.Id,
                SrcPath = ent.Path,
            };
            Capture(gc, g);   // first snapshot (entity is valid now)
            Pool.Add(g);
        }
        catch
        {
            // not a valid item
        }
    }

    // ---- Pool stabilization: merge fresh reads monotonically; freeze on settle or pickup -----------

    // Freeze a settled snapshot this many ticks after it last improved — but only once it actually has
    // mods, so an item whose Mods component streams in late still gets them (it keeps re-reading until
    // they arrive). Pickup freezes immediately via SourceMatches. MaxLifeTicks is an absolute backstop
    // that bounds per-item CPU for items that never gain mods (normal items) or never resolve at all.
    private const int SettleTicks = 120;
    private const int MaxLifeTicks = 600;

    // Called every Tick. Re-reads each unfrozen item from a FRESH ItemData and merges the result into
    // its snapshot (monotonically — see Merge), then freezes once the source entity is gone or the
    // snapshot has settled.
    public void RefreshPool(GameController gc)
    {
        if (gc?.InGame != true) return;

        foreach (var g in Pool)
        {
            if (g.Frozen) continue;
            g.AgeTicks++;

            // Source picked up / freed / its slot reused by a different entity: stop reading it and
            // keep the best snapshot we built while it was alive. On an immediate pickup the good
            // grab-time values are already merged in, and any half-torn-down read since then was
            // rejected by Merge — so the values can no longer reset to 0 / garbage.
            if (!SourceMatches(g))
            {
                Freeze(g);
                continue;
            }

            // Same live item: re-read it FRESH. A fresh ItemData re-reads the (possibly just-streamed)
            // Mods component, so mods that weren't loaded at the grab frame finally appear — this is
            // what fixes "some items never pull their mods".
            if (Capture(gc, g))
                g.StableTicks = 0;     // snapshot improved this tick; keep watching for more
            else
                g.StableTicks++;

            // Freeze once the snapshot HAS mods and has stopped improving, or at the absolute backstop.
            // Gating on "has mods" is what stops us freezing a class/base-only snapshot before a slow
            // Mods component streams in; the backstop still bounds CPU for genuinely mod-less items.
            if ((HasMods(g.Chips) && g.StableTicks >= SettleTicks) || g.AgeTicks >= MaxLifeTicks)
                Freeze(g);
        }
    }

    private static bool HasMods(List<Chip> chips)
    {
        foreach (var c in chips) if (c.Category == ChipCategory.Mod) return true;
        return false;
    }

    private static void Freeze(GrabbedItem g)
    {
        g.Frozen = true;
        g.Source = null;   // drop the live handle; the snapshot is now final
    }

    // True only while the grabbed item's source entity is still the same live item. A freed or reused
    // slot reads IsValid=false, Id=0, or a different Id/Path — all of which fail this check.
    private static bool SourceMatches(GrabbedItem g)
    {
        try
        {
            var ent = g.Source;
            return ent != null
                && ent.Address == g.SrcAddress
                && ent.IsValid
                && ent.Id != 0
                && ent.Id == g.SrcId
                && ent.Path == g.SrcPath;
        }
        catch { return false; }
    }

    // Re-read the live entity into a FRESH ItemData (so newly-streamed mods/stats are seen) and merge.
    // Returns true if the merge improved the snapshot.
    private static bool Capture(GameController gc, GrabbedItem g)
    {
        ItemData data;
        try { data = new ItemData(g.Source, gc); } catch { return false; }

        List<Chip> fresh;
        try { fresh = ChipsForItem(data).ToList(); } catch { return false; }

        return Merge(g, fresh);
    }

    // Monotonic merge of a fresh read into the snapshot, keyed by DedupKey: a chip is ADDED when first
    // seen and UPGRADED when a higher-quality value arrives, but an already-good value is NEVER replaced
    // by a lower-quality one and a chip is NEVER dropped. That is what makes the snapshot stable: a
    // teardown read (values gone to 0, or garbage) is quality 0 and simply ignored, while a late read
    // that finally carries the real roll upgrades the placeholder. Returns true if anything changed.
    private static bool Merge(GrabbedItem g, List<Chip> fresh)
    {
        var best = new Dictionary<string, Chip>();
        foreach (var c in g.Chips)
            if (!string.IsNullOrEmpty(c?.DedupKey)) best[c.DedupKey] = c;

        var changed = false;
        foreach (var c in fresh)
        {
            if (string.IsNullOrEmpty(c?.DedupKey)) continue;
            var qf = Quality(c);
            if (qf == 0) continue;                                  // drop zero/garbage; a later read upgrades
            if (!best.TryGetValue(c.DedupKey, out var ex)) { best[c.DedupKey] = c; changed = true; continue; }

            // Rarity is single-valued and loads late (a "Normal" placeholder before the Mods component
            // streams, then the real rarity). Keep the higher rank so the placeholder neither lingers as
            // a duplicate chip nor keeps flapping the snapshot (which would stall the settle-freeze).
            if (c.Category == ChipCategory.Rarity)
            {
                if (RarityRank(c.Display) > RarityRank(ex.Display)) { best[c.DedupKey] = c; changed = true; }
                continue;
            }

            if (Quality(ex) < qf) { best[c.DedupKey] = c; changed = true; }
        }

        if (changed)
            g.Chips = best.Values.OrderBy(c => c.Category).ThenBy(c => c.Display).ToList();
        return changed;
    }

    // Confidence in a chip's value, used to merge monotonically:
    //   0 = unusable: garbage text, or a mod whose rolled value is 0 (not yet streamed, or torn down)
    //   1 = a present value with no range to verify, or non-zero but outside its declared [min,max]
    //   2 = a non-zero value inside the mod's declared [min,max] — a trustworthy roll
    // Attribute/scalar chips (class, base, rarity, item level, ...) carry no live mod value; any
    // non-garbage one is quality 1 — kept on first sight, never replaced.
    private static int Quality(Chip c)
    {
        if (c == null || LooksGarbage(c)) return 0;
        if (c.Mod == null) return 1;

        var t = c.Mod.Threshold;
        if (t == 0) return 0;
        if (c.Mod.Min is int mn && c.Mod.Max is int mx)
            return t >= Math.Min(mn, mx) && t <= Math.Max(mn, mx) ? 2 : 1;
        return 1;
    }

    // Ordinal rank for the rarity chip's value, so the merge prefers the "more loaded" rarity (the real
    // one) over the "Normal" placeholder a not-yet-streamed item briefly reports.
    private static int RarityRank(string rarity) => rarity switch
    {
        "Normal" => 0,
        "Magic" => 1,
        "Rare" => 2,
        "Unique" => 3,
        _ => 0,
    };

    // A chip whose text betrays a bad memory read: an unresolved stat description ("<unknown ...>",
    // a leaked CachedStatDescription / collection ToString), scientific notation, or an absurdly
    // large number. Legit stats are small integers, so none of these survive a clean read.
    private static readonly Regex RxSci = new(@"[eE][+\-]?\d", RegexOptions.Compiled);
    private static readonly Regex RxBigNumber = new(@"\d{7,}", RegexOptions.Compiled);

    private static bool LooksGarbage(Chip c)
    {
        if (LooksGarbageText(c?.Display)) return true;

        // Absurd magnitudes in the roll OR its declared range betray a torn-down read whose number
        // still happens to be short enough to dodge the digit-run check (e.g. a stray 5-6 digit value).
        if (c?.Mod != null)
        {
            if (Math.Abs((long)c.Mod.Threshold) > 1_000_000) return true;
            if (c.Mod.Min is int mn && Math.Abs((long)mn) > 1_000_000) return true;
            if (c.Mod.Max is int mx && Math.Abs((long)mx) > 1_000_000) return true;
        }
        return false;
    }

    // True if a display string betrays a bad read: an unresolved stat description ("<unknown ...>", a
    // leaked CachedStatDescription / collection ToString), scientific notation, or a >=7-digit run.
    private static bool LooksGarbageText(string d)
    {
        if (string.IsNullOrEmpty(d)) return false;
        if (d.IndexOf("<unknown", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (d.IndexOf("CachedStatDescription", StringComparison.Ordinal) >= 0) return true;
        if (d.IndexOf("System.Collections", StringComparison.Ordinal) >= 0) return true;
        if (RxSci.IsMatch(d)) return true;
        if (RxBigNumber.IsMatch(d)) return true;
        return false;
    }

    // All chips for a single item, in render order (Class, Base, Rarity, scalar stats, flags, sockets,
    // then mods), scoped to AttributeCatalog. Each section reads the live ItemData defensively: a
    // freshly-encountered item whose Mods component the game hasn't streamed into memory yet throws on
    // the mod/rarity/flag reads. Isolating each section means such a failure degrades only that section
    // — the readable sections still render, and the live ItemData fills in the rest on a later frame.
    internal static IEnumerable<Chip> ChipsForItem(ItemData item)
    {
        var className = TryRead(() => item.ClassName);
        if (!string.IsNullOrEmpty(className))
            yield return new Chip { Category = ChipCategory.Class, Display = className,
                Attribute = "ClassName", Operator = "==", Value = className,
                DedupKey = "Class:" + className };

        var baseName = TryRead(() => item.BaseName);
        if (!string.IsNullOrEmpty(baseName))
            yield return new Chip { Category = ChipCategory.Base, Display = baseName,
                Attribute = "BaseName", Operator = "==", Value = baseName,
                DedupKey = "Base:" + baseName };

        var path = TryRead(() => item.Path);
        if (!string.IsNullOrEmpty(path))
            yield return new Chip { Category = ChipCategory.Base, Display = path,
                Attribute = "Path", Operator = "==", Value = path,
                DedupKey = "Path:" + path };

        // Rarity lives on the Mods component (like the mod list), so it resolves on the same frame the
        // mods do; Unknown means "not loaded / not applicable" — emit nothing then. Only emit for the
        // rarities the catalog supports so the "+" always compiles to a valid query.
        var rarity = TryRead(() => item.Rarity, ItemRarity.Unknown);
        var rarityName = rarity.ToString();
        if (rarity != ItemRarity.Unknown &&
            AttributeCatalog.Find("Rarity")?.EnumValues?.Contains(rarityName) == true)
            yield return new Chip { Category = ChipCategory.Rarity, Display = rarityName,
                Attribute = "Rarity", Operator = "==", Value = rarityName,
                DedupKey = "Rarity"   // category-only: an item has ONE rarity; merge keeps the higher rank
            };

        List<Chip> stats;
        try { stats = StatChips(item).ToList(); } catch { stats = []; }
        foreach (var chip in stats)
            yield return chip;

        List<(string key, string display, string op)> flags;
        try { flags = BoolFlags(item).ToList(); } catch { flags = []; }
        foreach (var (key, display, op) in flags)
            yield return new Chip { Category = ChipCategory.Flag, Display = display,
                Attribute = key, Operator = op, Value = null, DedupKey = "Flag:" + display };

        // PoE2 SocketData exposes SocketNumber only (no link size); emit a socket-count chip.
        var socket = TryRead(() => item.SocketInfo);
        if (socket != null && socket.SocketNumber > 0)
            yield return new Chip { Category = ChipCategory.Socket, Display = $"{socket.SocketNumber} sockets",
                Attribute = "Sockets", Operator = ">=", Value = socket.SocketNumber.ToString(),
                DedupKey = "Socket:n" + socket.SocketNumber };

        List<Chip> mods;
        try { mods = ModChips(item).ToList(); } catch { mods = []; }
        foreach (var chip in mods)
            yield return chip;
    }

    // Reads one live ItemData property in isolation. A transient memory-read failure (component not yet
    // streamed, or IFL throwing on a malformed item) yields the fallback instead of throwing, so one
    // unreadable section never discards the rest of the item's chips.
    private static T TryRead<T>(Func<T> read, T fallback = default)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private static IEnumerable<(string key, string display, string op)> BoolFlags(ItemData item)
    {
        // Only the flags in the catalog, emitted only when true ("has").
        if (item.IsCorrupted)  yield return ("IsCorrupted", "Corrupted", "has");
        if (item.IsIdentified) yield return ("IsIdentified", "Identified", "has");
        if (item.IsWeapon)     yield return ("IsWeapon", "Weapon", "has");
        if (item.Enchanted)    yield return ("Enchanted", "Enchanted", "has");
    }

    private static IEnumerable<Chip> ModChips(ItemData item)
    {
        var seen = new HashSet<string>();
        var chips = new List<Chip>();
        foreach (var mod in CollectMods(item))
        {
            try
            {
                if (mod == null) continue;

                var group = TryRead(() => mod.Group);
                var token = !string.IsNullOrEmpty(group) ? group : TryRead(() => mod.RawName) ?? TryRead(() => mod.Name);
                if (string.IsNullOrEmpty(token)) continue;
                // Skip placeholder/display-only stats (e.g. "DummyStatDisplayNothing").
                if (token.IndexOf("Dummy", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!seen.Add(token)) continue;   // dedup the unioned mod lists by identity

                // Read each field defensively. A single mod whose live .Translation read throws OR
                // resolves to garbage must still appear — with a fallback label and its real rolled
                // value — instead of vanishing.
                var translation = TryRead(() => mod.Translation);
                if (LooksGarbageText(translation)) translation = null;
                var displayName = TryRead(() => mod.DisplayName);
                var display = !string.IsNullOrEmpty(translation) ? translation
                            : !string.IsNullOrEmpty(displayName) ? displayName
                            : Humanize(token);

                var vmm = TryRead(() => mod.ValuesMinMax);
                int? min = null, max = null;
                if (vmm is { Length: > 0 }) { min = vmm[0].Min; max = vmm[0].Max; }
                var values = TryRead(() => mod.Values);
                var rolled = values is { Count: > 0 } ? values[0] : 0;

                chips.Add(new Chip
                {
                    Category = ChipCategory.Mod,
                    Display = display,
                    DedupKey = "Mod:" + token,
                    Mod = new ModRef
                    {
                        Display = !string.IsNullOrEmpty(displayName) ? displayName : (group ?? token),
                        MatchToken = group ?? token,
                        Mode = "Value",
                        ValueIndex = 0,
                        Threshold = rolled,
                        Min = min,
                        Max = max,
                    }
                });
            }
            catch { /* one unreadable mod must not drop the rest */ }
        }

        return chips;
    }

    // Enumerate the item's mods straight from the live Mods component (read fresh each capture so mods
    // that stream in after the grab are picked up). Falls back to the ModsNames -> FindMods resolution
    // if the component isn't readable.
    private static IEnumerable<ItemMod> CollectMods(ItemData item)
    {
        Mods comp = null;
        try { comp = item?.Entity?.GetComponent<Mods>(); } catch { comp = null; }

        if (comp != null)
        {
            // Union all the component's mod lists, typed lists first so ModChips' first-seen dedup keeps
            // their authoritative rolled values; ItemMods last only to backfill any type not yet covered.
            var combined = new List<ItemMod>();
            void Add(List<ItemMod> src) { if (src != null) combined.AddRange(src); }
            try { Add(comp.ExplicitMods); } catch { }
            try { Add(comp.ImplicitMods); } catch { }
            try { Add(comp.EnchantedMods); } catch { }
            try { Add(comp.SynthesisMods); } catch { }
            try { Add(comp.ItemMods); } catch { }
            if (combined.Count > 0) return combined;
        }

        // Fallback: the original ModsNames -> FindMods resolution.
        var viaNames = new List<ItemMod>();
        var names = item?.ModsNames ?? new List<string>();
        foreach (var name in names)
        {
            List<ItemMod> found;
            try { found = item.FindMods(name); } catch { continue; }
            if (found != null) viaNames.AddRange(found);
        }
        return viaNames;
    }

    // ---- Scalar catalog stat chips ----------------------------------------------------------------

    // The header scalars the editor can filter on (Item Level, Quality, sockets/armour/... handled
    // elsewhere). Each emits a ">= current value" chip, gated on the catalog containing the key.
    internal static IEnumerable<Chip> StatChips(ItemData item)
    {
        var ilvl = TryRead(() => item.ItemLevel, 0);
        if (ilvl > 0 && AttributeCatalog.Find("ItemLevel") != null)
            yield return ScalarChip("ItemLevel", "Item Level", ilvl);

        var quality = TryRead(() => item.ItemQuality, 0);
        if (quality > 0 && AttributeCatalog.Find("ItemQuality") != null)
            yield return ScalarChip("ItemQuality", "Quality", quality);

        var veiled = TryRead(() => item.VeiledModCount, 0);
        if (veiled > 0 && AttributeCatalog.Find("VeiledModCount") != null)
            yield return ScalarChip("VeiledModCount", "Veiled Mods", veiled);

        var stack = TryRead(() => item.StackInfo);
        if (stack != null && stack.Count > 0 && AttributeCatalog.Find("StackSize") != null)
            yield return ScalarChip("StackSize", "Stack Size", stack.Count);

        // MapInfo.Tier is the one MapInfo field IFL reliably fills.
        var map = TryRead(() => item.MapInfo);
        if (map != null && TryRead(() => map.IsMap, false) && map.Tier > 0 &&
            AttributeCatalog.Find("MapTier") != null)
            yield return ScalarChip("MapTier", "Map Tier", map.Tier);

        var arm = TryRead(() => item.ArmourInfo);
        if (arm != null)
        {
            if (arm.Armour > 0 && AttributeCatalog.Find("Armour") != null)
                yield return ScalarChip("Armour", "Base Armour", arm.Armour);
            if (arm.Evasion > 0 && AttributeCatalog.Find("Evasion") != null)
                yield return ScalarChip("Evasion", "Base Evasion", arm.Evasion);
            if (arm.ES > 0 && AttributeCatalog.Find("EnergyShield") != null)
                yield return ScalarChip("EnergyShield", "Base Energy Shield", arm.ES);
        }
    }

    // Scalar attribute chip (Item Level, Quality, Map Tier, ...). Display is the bare label; the card
    // appends ">= value" from Operator/Value. Compiles via AttributeCatalog (caller checks the key).
    private static Chip ScalarChip(string attrKey, string display, int value) => new()
    {
        Category = ChipCategory.Stat,
        Display = display,
        Attribute = attrKey,
        Operator = ">=",
        Value = value.ToString(),
        DedupKey = "Stat:" + attrKey,
    };

    // Splits a PascalCase identifier into words, e.g. "ItemDropRarity" -> "Item Drop Rarity".
    private static string Humanize(string enumName)
    {
        if (string.IsNullOrEmpty(enumName)) return enumName;
        var sb = new System.Text.StringBuilder(enumName.Length + 8);
        for (var i = 0; i < enumName.Length; i++)
        {
            var c = enumName[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(enumName[i - 1])) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }
}

