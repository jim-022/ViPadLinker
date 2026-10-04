using System;
using System.Collections.Generic;
using System.Drawing;

// ─── TrayApp: status icons ────────────────────────────────────────────────────
// All NotifyIcon bitmaps, generated once at startup from assets/Icon.ico and
// cached for the process lifetime. Two badge styles: plain colored dot / large
// colored digit for the non-link states, twin pad dots for link mode.
// Partial class — state (link, viPads) lives in TrayApp.cs.

static partial class TrayApp
{
    // state icons — base app icon with a colored marker, generated once.
    // Link mode: twin pad dots (_iconLinkDots[left, right]) over the shared
    // grey/green/red set — left dot = Pad 1, right dot = Pad 2, so a lost pad is
    // identifiable at a glance (the old digit badges could not say WHICH pad was gone).
    // Holder / idle mode: a 4-slot map, one dot per XInput slot in a 2x2 grid
    // (numpad order: 1,2 bottom / 3,4 top). Built on demand, then cached — see
    // SlotMapIcon.
    static readonly Icon[,] _iconLinkDots = new Icon[3, 3]; // [PadDot, PadDot] — built once

    // twin pad dots in link mode — shared 3-color set, one per pad state
    static readonly Color ColorPadMissing = Color.Gray;     // not connected (yet)
    static readonly Color ColorPadLive    = Color.LimeGreen;
    static readonly Color ColorPadGone    = Color.OrangeRed; // disconnected
    const int PadDotMissing = 0, PadDotLive = 1, PadDotGone = 2;

    // 4-slot map colors (holder / idle mode) — two drawn states:
    //   blue  = our ViPad
    //   green = a physical controller is in the slot, ours or not
    // FREE slots draw no dot at all — a dot only appears where a device is, and
    // when every slot is free the icon shows bare artwork (see SlotMapIcon).
    // There is deliberately NO disconnect/red state here: a ViPad cannot die and a
    // physical pad leaving a slot is normal, not a fault. Red belongs to link mode
    // only, where a pad we were actively merging input from really did go away.
    static readonly Color ColorSlotFree   = Color.Gray;       // never drawn — placeholder keeps palette index == state
    static readonly Color ColorSlotViPad  = Color.DodgerBlue; // our virtual pad
    static readonly Color ColorSlotPhys   = Color.LimeGreen;  // physical pad present
    const int SlotFree = 0, SlotViPad = 1, SlotPhys = 2;

    // The tray's own icon, kept so lazily-built slot-map icons can draw on it.
    static Icon? _baseIcon;

    // Lazy icon cache for the slot map: 3^4 = 81 combinations, but only a handful
    // ever occur. Building all 81 at startup would paint 81 bitmaps + 81 HICONs for
    // layouts that never happen, so each is drawn the first time it appears and kept
    // for the process lifetime. Key = base-3 slot code (see SlotMapKey).
    // Single-threaded by construction: UpdateStatus only ever runs on the UI thread
    // (direct calls, or background events marshaled through _sync.Post).
    static readonly Dictionary<int, Icon> _slotMapIcons = new();

    // ── Status icons ──────────────────────────────────────────────────────────

    static void BuildStateIcons(Icon baseIcon)
    {
        _baseIcon = baseIcon;

        // Link mode: every (left, right) combination pre-baked — the tray gets
        // exactly one bitmap per state, there are no live layers to compose.
        // 3 shared colors x 3 = 9 icons; unreachable combos (right live while
        // left missing) cost nothing and keep the lookup branch-free.
        var dotColors = new[] { ColorPadMissing, ColorPadLive, ColorPadGone };
        for (int l = 0; l < 3; l++)
            for (int r = 0; r < 3; r++)
                _iconLinkDots[l, r] = WithTwinDots(baseIcon, dotColors[l], dotColors[r]);
    }

    // Per-slot state for the 4-slot map: our ViPad, a physical pad we are not
    // using, or free. Reads the same sources as the status text and the menu
    // strip, so icon, words and strip can never disagree. 4 XInputGetState calls
    // — fine, this runs only on menu open / after an action, never in a loop.
    static int[] SlotStates()
    {
        var viPadSlots = new HashSet<uint>();
        lock (viPads)
            foreach (var pad in viPads)
                try { viPadSlots.Add((uint)pad.UserIndex); } catch { }

        var states = new int[4];
        for (int s = 0; s < 4; s++)
        {
            if (viPadSlots.Contains((uint)s)) states[s] = SlotViPad;
            else if (XInput.GetState((uint)s, out _)) states[s] = SlotPhys;
            else states[s] = SlotFree;
        }
        return states;
    }

    static int SlotMapKey(int[] states)
    {
        int key = 0;
        for (int s = 0; s < 4; s++) key = key * 3 + states[s];
        return key;
    }

    // Slot map icon for the current layout, drawn on first use and cached after.
    // All slots free = the BARE app icon: dots only carry information when there is
    // something in a slot, so an empty machine shows plain artwork. Returning the
    // cached base instance also keeps UpdateStatus's reference comparison cheap.
    static Icon SlotMapIcon()
    {
        var states = SlotStates();
        int key = SlotMapKey(states);
        if (key == 0) return _baseIcon!;   // nothing anywhere — no dots
        if (_slotMapIcons.TryGetValue(key, out var cached)) return cached;

        var icon = WithSlotMap(_baseIcon!, states);
        _slotMapIcons[key] = icon;
        Log.Info($"[tray] slot-map icon built (key {key}): " +
                 $"{states[0]}/{states[1]}/{states[2]}/{states[3]}");
        return icon;
    }

    // Four status dots in a 2x2 grid, numpad order: slot 1 bottom-left, 2
    // bottom-right, 3 top-left, 4 top-right. Bottom row matches the link-mode
    // twin dots, so the two modes agree along the bottom edge. Dots keep the
    // twin-dot size and dark ring; the artwork (two gamepads + cable tip) fills
    // all four corners, so the ring is what keeps them readable.
    static Icon WithSlotMap(Icon baseIcon, int[] states)
    {
        var palette = new[] { ColorSlotFree, ColorSlotViPad, ColorSlotPhys };
        const int Size = 32;
        const int Dot  = 13;
        using var src = baseIcon.ToBitmap();
        var bmp = new Bitmap(Size, Size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, Size, Size);

            using var pen = new Pen(Color.FromArgb(30, 30, 30), 2);
            // numpad order: index 0 = slot 1 = bottom-left
            var rects = new[]
            {
                new Rectangle(0,             Size - Dot - 1, Dot, Dot), // slot 1
                new Rectangle(Size - Dot - 1, Size - Dot - 1, Dot, Dot), // slot 2
                new Rectangle(0,             0,              Dot, Dot), // slot 3
                new Rectangle(Size - Dot - 1, 0,              Dot, Dot), // slot 4
            };
            for (int s = 0; s < 4; s++)
            {
                // Free slots draw NOTHING — a dot only appears where a device is.
                // (Slot position still identifies WHICH slot; the all-free case never
                // reaches here, SlotMapIcon returns the bare icon for it.)
                if (states[s] == SlotFree) continue;
                using var fill = new SolidBrush(palette[states[s]]);
                g.FillEllipse(fill, rects[s]);
                g.DrawEllipse(pen, rects[s]);
            }
        }
        IntPtr hicon = bmp.GetHicon();
        bmp.Dispose();
        return Icon.FromHandle(hicon);
    }

    // Two status dots at the bottom corners of the 32px icon: left = Pad 1,
    // right = Pad 2. Same dark-edge + white-ring treatment as the single dot
    // so they stay visible on light taskbars at the tray's 16px render size.
    static Icon WithTwinDots(Icon baseIcon, Color left, Color right)
    {
        const int Size = 32;
        const int Dot  = 13;
        using var src = baseIcon.ToBitmap();
        var bmp = new Bitmap(Size, Size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, Size, Size);

            using var pen = new Pen(Color.FromArgb(30, 30, 30), 2);
            var rectL = new Rectangle(0, Size - Dot - 1, Dot, Dot);
            var rectR = new Rectangle(Size - Dot - 1, Size - Dot - 1, Dot, Dot);
            using (var fill = new SolidBrush(left))
            {
                g.FillEllipse(fill, rectL);
                g.DrawEllipse(pen, rectL);
            }
            using (var fill = new SolidBrush(right))
            {
                g.FillEllipse(fill, rectR);
                g.DrawEllipse(pen, rectR);
            }
        }
        // GetHicon copies into an unmanaged HICON; the handles live for the
        // process lifetime (Icon.FromHandle does not own them) — 9 icons, once.
        IntPtr hicon = bmp.GetHicon();
        bmp.Dispose();
        return Icon.FromHandle(hicon);
    }

    // Twin-dot state for one link pad index: grey until it joins the session,
    // then green/red straight from PadAlive — the same signal the status text
    // uses, so icon and words can never disagree. Disconnect shows red
    // immediately (first failed read); the 2s freeze/zeroing inside LinkSession
    // is an input-safety detail and deliberately not visualised.
    static int PadDotState(int padIndex)
    {
        if (link == null) return PadDotMissing;
        if (link.PadCount <= padIndex) return PadDotMissing;
        return link.PadAlive(padIndex) ? PadDotLive : PadDotGone;
    }
}
