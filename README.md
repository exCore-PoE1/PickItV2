# PickItV2

An ExileCore plugin for Path of Exile 1 that picks up ground items using ItemFilterLibrary rules.

## Features

- Manual and lazy-looting modes.
- Ordered `.ifl` rule files with per-file enable controls.
- In-game GUI filter editor with lossless raw-row handling.
- Hovered-item scanner for creating rules from live item attributes and mods.
- Automatic validation before editor changes are saved.
- Inventory-space preview and configurable chest/object interaction.
- Optional pickup of matching items already under the cursor.

Open the plugin settings and select **Edit** beside a rule file to launch the filter editor. The default item-grab hotkey is `G`.

## Build

```powershell
dotnet build
```

This PoE1 port retains history from [exApiTools/PickItV2](https://github.com/exApiTools/PickItV2).

