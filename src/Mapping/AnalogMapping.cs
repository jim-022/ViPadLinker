using System;
using System.Collections.Generic;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

// ─── Analog mapping ───────────────────────────────────────────────────────────

enum AnalogSource     { LT, RT, LStick, RStick }
enum AnalogTargetKind { Block, Swap, Button }

struct AnalogEntry
{
    public AnalogTargetKind Kind;
    public Xbox360Button    Button; // used when Kind == Button
}

// maps analog sources (LT, RT, LStick, RStick) to targets
// also maps digital buttons to max trigger output (A = LT)
class AnalogMapping
{
    readonly Dictionary<AnalogSource, AnalogEntry> _analogMap;
    readonly Dictionary<Xbox360Button, bool>       _buttonToTrigger; // true = LT, false = RT

    public AnalogMapping(
        Dictionary<AnalogSource, AnalogEntry> analogMap,
        Dictionary<Xbox360Button, bool>       buttonToTrigger)
    {
        _analogMap       = analogMap;
        _buttonToTrigger = buttonToTrigger;
    }

    // apply analog mappings — modifies trigger and stick values in place
    // called after button apply so button → trigger combos work correctly
    public void Apply(
        IXbox360Controller pad,
        ref byte  lt,  ref byte  rt,
        ref short lx,  ref short ly,
        ref short rx,  ref short ry,
        ushort buttons)
    {
        // snapshot raw values before any modifications
        byte  rawLt = lt; byte  rawRt = rt;
        short rawLx = lx; short rawLy = ly;
        short rawRx = rx; short rawRy = ry;

        // LT
        if (_analogMap.TryGetValue(AnalogSource.LT, out var ltEntry))
        {
            switch (ltEntry.Kind)
            {
                case AnalogTargetKind.Block:
                    lt = 0;
                    break;
                case AnalogTargetKind.Swap:
                    lt = rawRt;
                    rt = rawLt; // swap both sides at once
                    break;
                case AnalogTargetKind.Button:
                    if (rawLt > 0) pad.SetButtonState(ltEntry.Button, true);
                    lt = 0;
                    break;
            }
        }

        // RT — only handle swap if LT didn't already cover it
        if (_analogMap.TryGetValue(AnalogSource.RT, out var rtEntry))
        {
            switch (rtEntry.Kind)
            {
                case AnalogTargetKind.Block:
                    rt = 0;
                    break;
                case AnalogTargetKind.Swap:
                    if (!_analogMap.ContainsKey(AnalogSource.LT))
                    {
                        rt = rawLt;
                        lt = rawRt; // swap both sides at once
                    }
                    break;
                case AnalogTargetKind.Button:
                    if (rawRt > 0) pad.SetButtonState(rtEntry.Button, true);
                    rt = 0;
                    break;
            }
        }

        // LStick
        if (_analogMap.TryGetValue(AnalogSource.LStick, out var lsEntry))
        {
            switch (lsEntry.Kind)
            {
                case AnalogTargetKind.Block:
                    lx = 0; ly = 0;
                    break;
                case AnalogTargetKind.Swap:
                    lx = rawRx; ly = rawRy;
                    rx = rawLx; ry = rawLy; // swap both sides at once
                    break;
            }
        }

        // RStick — only handle swap if LStick didn't already cover it
        if (_analogMap.TryGetValue(AnalogSource.RStick, out var rsEntry))
        {
            switch (rsEntry.Kind)
            {
                case AnalogTargetKind.Block:
                    rx = 0; ry = 0;
                    break;
                case AnalogTargetKind.Swap:
                    if (!_analogMap.ContainsKey(AnalogSource.LStick))
                    {
                        rx = rawLx; ry = rawLy;
                        lx = rawRx; ly = rawRy; // swap both sides at once
                    }
                    break;
            }
        }

        // digital button → max trigger press
        foreach (var kv in _buttonToTrigger)
        {
            if (!IsButtonPressed(kv.Key, buttons)) continue;
            if (kv.Value) lt = Math.Max(lt, (byte)255);
            else          rt = Math.Max(rt, (byte)255);
        }
    }

    static bool IsButtonPressed(Xbox360Button btn, ushort raw)
    {
        foreach (var (bit, b) in ButtonMapping.AllButtons)
            if (b == btn) return (raw & bit) != 0;
        return false;
    }

    public IReadOnlyDictionary<AnalogSource, AnalogEntry> AnalogMap       => _analogMap;
    public IReadOnlyDictionary<Xbox360Button, bool>       ButtonToTrigger => _buttonToTrigger;
}
