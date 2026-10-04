using System.Collections.Generic;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

// ─── Button mapping ───────────────────────────────────────────────────────────

// maps a physical button press to a (possibly remapped) virtual button
// if a button is not in the remap dict it passes through unchanged
class ButtonMapping
{
    readonly Dictionary<Xbox360Button, Xbox360Button> _remap;
    readonly bool _hasRemap;

    // all button bit → button pairs — used in Linked mode and by AnalogMapping
    internal static readonly (ushort bit, Xbox360Button btn)[] AllButtons =
    {
        (0x0001, Xbox360Button.Up),
        (0x0002, Xbox360Button.Down),
        (0x0004, Xbox360Button.Left),
        (0x0008, Xbox360Button.Right),
        (0x0010, Xbox360Button.Start),
        (0x0020, Xbox360Button.Back),
        (0x0040, Xbox360Button.LeftThumb),
        (0x0080, Xbox360Button.RightThumb),
        (0x0100, Xbox360Button.LeftShoulder),
        (0x0200, Xbox360Button.RightShoulder),
        (0x1000, Xbox360Button.A),
        (0x2000, Xbox360Button.B),
        (0x4000, Xbox360Button.X),
        (0x8000, Xbox360Button.Y),
    };

    // left side subset — used in Split mode for pad 1
    static readonly (ushort bit, Xbox360Button btn)[] LeftButtons =
    {
        (0x0001, Xbox360Button.Up),
        (0x0002, Xbox360Button.Down),
        (0x0004, Xbox360Button.Left),
        (0x0008, Xbox360Button.Right),
        (0x0020, Xbox360Button.Back),
        (0x0040, Xbox360Button.LeftThumb),
        (0x0100, Xbox360Button.LeftShoulder),
    };

    // right side subset — used in Split mode for pad 2
    static readonly (ushort bit, Xbox360Button btn)[] RightButtons =
    {
        (0x0010, Xbox360Button.Start),
        (0x0080, Xbox360Button.RightThumb),
        (0x0200, Xbox360Button.RightShoulder),
        (0x1000, Xbox360Button.A),
        (0x2000, Xbox360Button.B),
        (0x4000, Xbox360Button.X),
        (0x8000, Xbox360Button.Y),
    };

    public ButtonMapping(Dictionary<Xbox360Button, Xbox360Button> remap)
    {
        _remap    = remap;
        _hasRemap = remap.Count > 0;
    }

    // compute the remapped virtual button mask WITHOUT touching the pad.
    // Linked mode computes both pads' masks, ORs them, then applies once —
    // no clear-then-set churn, each button gets its final state exactly once.
    public ushort ComputeTargets(ushort raw)
    {
        ushort result = 0;
        foreach (var (bit, src) in AllButtons)
        {
            if ((raw & bit) == 0) continue;
            var target = _hasRemap && _remap.TryGetValue(src, out var mapped) ? mapped : src;
            if (target == Xbox360Button.Guide) continue; // none — blocked
            result |= BitFor(target);
        }
        return result;
    }

    static ushort BitFor(Xbox360Button btn)
    {
        foreach (var (bit, b) in AllButtons)
            if (b == btn) return bit;
        return 0;
    }

    // set every button to its final state in a single pass
    public static void ApplyMask(IXbox360Controller pad, ushort mask)
    {
        foreach (var (bit, btn) in AllButtons)
            pad.SetButtonState(btn, (mask & bit) != 0);
    }

    // apply left-side buttons only (Split mode pad 1)
    public void ApplyLeft(IXbox360Controller pad, ushort raw)
    {
        foreach (var (_, btn) in LeftButtons)
            pad.SetButtonState(btn, false);

        foreach (var (bit, src) in LeftButtons)
        {
            if ((raw & bit) == 0) continue;
            var target = _hasRemap && _remap.TryGetValue(src, out var mapped) ? mapped : src;
            if (target == Xbox360Button.Guide) continue; // none — blocked
            pad.SetButtonState(target, true);
        }
    }

    // apply right-side buttons only (Split mode pad 2)
    public void ApplyRight(IXbox360Controller pad, ushort raw)
    {
        foreach (var (_, btn) in RightButtons)
            pad.SetButtonState(btn, false);

        foreach (var (bit, src) in RightButtons)
        {
            if ((raw & bit) == 0) continue;
            var target = _hasRemap && _remap.TryGetValue(src, out var mapped) ? mapped : src;
            if (target == Xbox360Button.Guide) continue; // none — blocked
            pad.SetButtonState(target, true);
        }
    }

    public IReadOnlyDictionary<Xbox360Button, Xbox360Button> Remap => _remap;
}
