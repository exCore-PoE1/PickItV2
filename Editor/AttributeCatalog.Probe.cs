using System;
using System.Collections.Generic;

namespace PickIt.Editor;

public static partial class AttributeCatalog
{
    // Drops entries whose DSL doesn't compile against the live ItemFilterLibrary build,
    // so guessed accessors never ship broken. Call once at plugin load.
    public static void ProbeAndPrune(Action<string> logDropped = null)
    {
        var kept = new List<AttrDef>();
        foreach (var d in All)
        {
            var probe = d.Kind switch
            {
                AttrKind.Number => $"{d.Dsl} >= 0",
                AttrKind.Bool   => d.Dsl,
                AttrKind.Enum when d.EnumValues is { Length: > 0 }
                                => $"{d.Dsl} == {d.EnumType}.{d.EnumValues[0]}",
                AttrKind.Enum   => null,   // malformed enum entry -> fails Validate -> pruned + logged
                _               => $"{d.Dsl} == \"x\"",
            };
            if (QueryValidator.Validate(probe).ok) kept.Add(d);
            else logDropped?.Invoke(d.Key);
        }
        All.Clear();
        All.AddRange(kept);
    }
}

