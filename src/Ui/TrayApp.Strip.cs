using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

// ─── TrayApp: slot strip row ──────────────────────────────────────────────────
// The [V]-parity header inside the context menu: one painted cell per XInput
// slot, drawn over the strip item's invisible placeholder text.
// Partial class — state (link, viPads) lives in TrayApp.cs.

static partial class TrayApp
{
    // current slot-strip tokens (L/S/V/1/2/!/P/·) for the owner-drawn strip row
    static string[] _stripTokens = { "·", "·", "·", "·" };

    // [V] parity as a compact header strip: one token per XInput slot.
    // L=link virtual pad (LINKED) · S=link virtual pad (SPLIT) · V=holder ViPad ·
    // 1/2=live link pad · !=link pad lost · P=physical pad · ·=empty
    static string[] SlotTokens()
    {
        var viPadSlots = new HashSet<uint>();
        foreach (var pad in viPads)
            try { viPadSlots.Add((uint)pad.UserIndex); } catch { }

        var t = new string[4];
        for (uint i = 0; i < 4; i++)
        {
            if (link != null && i == 0)
                t[i] = link.Mode == LinkMode.Linked ? "L" : "S";
            else if (link != null && link.PadIndexForSlot(i) >= 0)
            {
                int p = link.PadIndexForSlot(i);
                t[i] = link.PadAlive(p) ? (p + 1).ToString() : "!";
            }
            else if (viPadSlots.Contains(i))                  t[i] = "V";
            else if (XInput.GetState(i, out _))               t[i] = "P";
            else                                              t[i] = "·";
        }
        return t;
    }

    // ── Slot strip: filled circle cells, state-colored, bold white glyph —
    //    same visual language as the tray badge. Painted over the item's
    //    invisible placeholder text (Paint fires after default rendering);
    //    runs on every repaint of the row, which only happens per menu open.
    //    CellW == CellH is load-bearing: FillEllipse on a non-square rect
    //    draws an oval.
    const int CellW = 20, CellH = 20, CellGap = 6, StripPad = 8;

    static Color StripColor(string token) => token switch
    {
        "L" or "V"  => Color.DodgerBlue,   // our virtual pad / holder
        "S"         => Color.RoyalBlue,    // virtual pad in SPLIT (slightly deeper)
        "1" or "2"  => Color.LimeGreen,    // live link pad
        "!"         => Color.OrangeRed,    // link pad lost
        "P"         => Color.SlateGray,    // physical pad, not ours
        _           => Color.Transparent,  // empty
    };

    // Custom renderer: default professional look everywhere, except the slot
    // strip row — its (blank) text is replaced by painted cells.
    class StripCellRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!ReferenceEquals(e.Item, _slotStripItem))
            {
                base.OnRenderItemText(e);
                return;
            }
            // base NOT called — the placeholder spaces are never drawn
            PaintStripCells(e.Graphics, e.TextRectangle);
        }
    }

    // Paint-path resources created ONCE at startup. PaintStripCells runs on every
    // repaint of the row (menu open + each hover/selection change), and Font wraps
    // a GDI handle — allocating one per paint churns handles and feeds gen0.
    static readonly Font StripFont = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    static readonly StringFormat StripFmt = new StringFormat
    {
        Alignment     = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
    };
    static readonly Pen EmptyPen = new Pen(Color.FromArgb(60, SystemColors.GrayText), 1f);
    static readonly Dictionary<string, SolidBrush> CellBrushes = BuildCellBrushes();

    static Dictionary<string, SolidBrush> BuildCellBrushes()
    {
        var d = new Dictionary<string, SolidBrush>();
        foreach (var t in new[] { "L", "S", "V", "1", "2", "!", "P" })
            d[t] = new SolidBrush(StripColor(t));
        return d;
    }

    static void PaintStripCells(Graphics g, Rectangle textRect)
    {
        g.SmoothingMode     = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // start exactly where menu text starts — auto-aligned with the items below
        int x = textRect.Left;
        int y = textRect.Top + (textRect.Height - CellH) / 2;
        foreach (var t in _stripTokens)
        {
            var cell = new Rectangle(x, y, CellW, CellH);
            if (CellBrushes.TryGetValue(t, out var brush))
            {
                g.FillEllipse(brush, cell.X, cell.Y, CellW - 1, CellH - 1);
                g.DrawString(t, StripFont, Brushes.White, cell, StripFmt);
            }
            else
            {
                // empty slot: faint outline + dim dot, deliberately quiet
                g.DrawArc(EmptyPen, cell.X, cell.Y, CellW - 1, CellH - 1, 0, 360);
                g.DrawString("·", StripFont, Brushes.Gray, cell, StripFmt);
            }
            x += CellW + CellGap;
        }
    }

    const string SlotLegend =
        "Slots: L=link LINKED  S=link SPLIT  V=ViPad holder  1/2=link pad  !=pad lost  P=physical  ·=empty";

    // Placeholder text sized so the item's measured width matches the painted
    // cells (owner-draw paints inside whatever width the text reserved).
    // Computed once — the SAME string instance every time, so the Text setter's
    // equality check skips re-layout on every UpdateStatus.
    static string? _stripPlaceholder;
    static string StripPlaceholder()
    {
        if (_stripPlaceholder != null) return _stripPlaceholder;
        int needed = 4 * CellW + 3 * CellGap + 2 * StripPad;
        int spaceW = TextRenderer.MeasureText("          ", _slotStripItem.Font).Width / 10;
        if (spaceW <= 0) spaceW = 4;
        int n = Math.Max(1, needed / spaceW);
        return _stripPlaceholder = new string(' ', n);
    }
}
