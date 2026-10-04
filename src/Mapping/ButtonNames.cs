using System;
using System.Collections.Generic;
using Nefarius.ViGEm.Client.Targets.Xbox360;

// ─── Button name ↔ Xbox360Button ─────────────────────────────────────────────

static class ButtonNames
{
    // canonical name → ViGEm enum — used when parsing mapping.ini
    static readonly Dictionary<string, Xbox360Button> ByName =
        new Dictionary<string, Xbox360Button>(StringComparer.OrdinalIgnoreCase)
        {
            { "A",     Xbox360Button.A             },
            { "B",     Xbox360Button.B             },
            { "X",     Xbox360Button.X             },
            { "Y",     Xbox360Button.Y             },
            { "LB",    Xbox360Button.LeftShoulder  },
            { "RB",    Xbox360Button.RightShoulder },
            { "Start", Xbox360Button.Start         },
            { "Back",  Xbox360Button.Back          },
            { "LS",    Xbox360Button.LeftThumb     },
            { "RS",    Xbox360Button.RightThumb    },
            { "Up",    Xbox360Button.Up            },
            { "Down",  Xbox360Button.Down          },
            { "Left",  Xbox360Button.Left          },
            { "Right", Xbox360Button.Right         },
            { "None",  Xbox360Button.Guide         },
        };

    // ViGEm enum → canonical name — used when displaying mappings
    static readonly Dictionary<Xbox360Button, string> ByButton;

    static ButtonNames()
    {
        ByButton = new Dictionary<Xbox360Button, string>();
        foreach (var kv in ByName)
            ByButton[kv.Value] = kv.Key;
    }

    public static bool TryParse(string name, out Xbox360Button btn) =>
        ByName.TryGetValue(name.Trim(), out btn);

    public static string Name(Xbox360Button btn) =>
        ByButton.TryGetValue(btn, out var n) ? n : btn.ToString();
}
