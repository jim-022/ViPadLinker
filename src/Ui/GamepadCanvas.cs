using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// ─── Gamepad canvas ──────────────────────────────────────────────────────────
// GDI+ gamepad outline for the mapping editor. Generic silhouette
// — letters not colors, no vendor branding — so the art ages well. Geometry is
// authored once in virtual units, packed into a tight content box, and scaled
// to the control; painting and hit-testing share the same region table.
//
// The canvas is a SELECTOR, not an editor: clicking a control raises
// RegionClicked(id) and the editor focuses the matching dropdown; the editor
// pushes state back (SetState / SetSelected) so the art mirrors the config
// live — remapped = blue, blocked = red, conflict = orange, selected = accent.

enum RegionState { Default, Remapped, Blocked, Conflict }

sealed class GamepadCanvas : Control
{
    enum RegionKind { Circle, Ring, Rect }

    // "Zone", not "Region" — Control.Region already exists and a nested
    // Region would hide it (CS0108)
    sealed class Zone
    {
        public string Id = "";
        public string Label = "";
        public RectangleF Bounds;
        public RegionKind Kind;
        public float Inner;          // ring hole radius (Kind == Ring)
        public string? Text;         // glyph drawn centered
        public float TextSize = 12f;

        public bool Contains(PointF p)
        {
            if (Kind == RegionKind.Rect) return Bounds.Contains(p);
            float cx = Bounds.X + Bounds.Width / 2f, cy = Bounds.Y + Bounds.Height / 2f;
            float r = Bounds.Width / 2f, dx = p.X - cx, dy = p.Y - cy, d2 = dx * dx + dy * dy;
            return Kind == RegionKind.Circle ? d2 <= r * r : d2 <= r * r && d2 >= Inner * Inner;
        }
    }

    readonly List<Zone> _zones = new();
    readonly Dictionary<string, RegionState> _state = new();
    readonly Dictionary<string, string> _detail = new();
    readonly Dictionary<float, Font> _fonts = new();
    readonly ToolTip _tip = new();
    GraphicsPath? _body;
    RectangleF _content;          // tight bounds of body + zones + stroke
    string? _selected;
    Zone? _hover;

    public event Action<string>? RegionClicked;

    public GamepadCanvas()
    {
        DoubleBuffered = true;
        ResizeRedraw   = true;
        BackColor      = Color.White;
        BuildGeometry();
    }

    // ── geometry (virtual coords, draw order = add order, hit-test reverse) ──
    void BuildGeometry()
    {
        var p = new GraphicsPath();
        p.AddBezier(100, 112, 138, 70, 332, 70, 370, 112);   // top edge
        p.AddBezier(370, 112, 412, 155, 436, 248, 412, 304); // right outer
        p.AddBezier(412, 304, 400, 334, 358, 338, 336, 306); // right grip
        p.AddBezier(336, 306, 312, 274, 286, 260, 235, 260); // to bottom centre
        p.AddBezier(235, 260, 184, 260, 158, 274, 134, 306); // left inner grip
        p.AddBezier(134, 306, 112, 338, 70, 334, 58, 304);   // left grip
        p.AddBezier(58, 304, 34, 248, 58, 155, 100, 112);    // left outer
        p.CloseFigure();
        _body = p;

        // triggers + shoulders peek above the body edge (drawn behind it)
        AddRect("LT", "LT trigger", 134, 32, 56, 20, "LT");
        AddRect("RT", "RT trigger", 278, 32, 56, 20, "RT");
        AddRect("LB", "Left bumper", 120, 60, 76, 26, "LB");
        AddRect("RB", "Right bumper", 272, 60, 76, 26, "RB");

        // sticks: outer ring = analog motion, inner circle = stick click
        // (hit-test is reverse add order, so the inner circle wins its area)
        AddRing("LStick", "Left stick", 150, 138, 34, 22);
        AddCircle("LS", "Left stick click", 150, 138, 22, "L");
        AddRing("RStick", "Right stick", 288, 212, 34, 22);
        AddCircle("RS", "Right stick click", 288, 212, 22, "R");

        // D-pad arms (centre square is decoration, not clickable)
        AddRect("Up",    "D-pad up",    137, 186, 26, 24, "\u25B2", 9);
        AddRect("Down",  "D-pad down",  137, 236, 26, 24, "\u25BC", 9);
        AddRect("Left",  "D-pad left",  113, 210, 24, 26, "\u25C0", 9);
        AddRect("Right", "D-pad right", 163, 210, 24, 26, "\u25B6", 9);

        AddRect("Back",  "Back",  202, 122, 34, 20, "Back", 8);
        AddRect("Start", "Start", 242, 122, 34, 20, "Start", 8);

        // face buttons — added last so they win hit-tests on tiny overlaps;
        // smaller radius keeps the cluster tight and clear of the right stick
        AddCircle("Y", "Y", 332, 120, 13, "Y", 11);
        AddCircle("X", "X", 298, 150, 13, "X", 11);
        AddCircle("B", "B", 366, 150, 13, "B", 11);
        AddCircle("A", "A", 332, 180, 13, "A", 11);

        // tight content box = body + every zone, grown by the widest stroke
        // (4.5 when selected) plus a hair — no dead margin around the art
        _content = RectangleF.Empty;
        _content = RectangleF.Union(_content, _body!.GetBounds());
        foreach (var z in _zones)
            _content = RectangleF.Union(_content, z.Bounds);
        _content.Inflate(5f, 5f);
    }

    // width/height of the drawn art — the editor sizes the canvas column from
    // this so the panel hugs the gamepad instead of a fixed square
    public float ContentAspect => _content.Width / _content.Height;

    void AddRect(string id, string label, float x, float y, float w, float h, string? text = null, float ts = 11f)
        => _zones.Add(new Zone { Id = id, Label = label, Bounds = new RectangleF(x, y, w, h), Kind = RegionKind.Rect, Text = text, TextSize = ts });

    void AddCircle(string id, string label, float cx, float cy, float r, string? text = null, float ts = 12f)
        => _zones.Add(new Zone { Id = id, Label = label, Bounds = new RectangleF(cx - r, cy - r, r * 2, r * 2), Kind = RegionKind.Circle, Text = text, TextSize = ts });

    void AddRing(string id, string label, float cx, float cy, float rOuter, float rInner)
        => _zones.Add(new Zone { Id = id, Label = label, Bounds = new RectangleF(cx - rOuter, cy - rOuter, rOuter * 2, rOuter * 2), Kind = RegionKind.Ring, Inner = rInner });

    // ── public state API (editor → canvas) ──────────────────────────────────

    public void SetState(string id, RegionState st, string detail = "")
    {
        _state[id] = st;
        _detail[id] = detail;
    }

    public void SetSelected(string? id)
    {
        if (_selected == id) return;
        _selected = id;
        Invalidate();
    }

    // ── input ────────────────────────────────────────────────────────────────

    float ScaleF => Math.Min(Width / _content.Width, Height / _content.Height);

    PointF ToVirtual(Point pt)
    {
        float s = ScaleF;
        return new PointF((pt.X - (Width - _content.Width * s) / 2f) / s + _content.X,
                          (pt.Y - (Height - _content.Height * s) / 2f) / s + _content.Y);
    }

    Zone? Hit(Point pt)
    {
        var p = ToVirtual(pt);
        for (int i = _zones.Count - 1; i >= 0; i--)
            if (_zones[i].Contains(p)) return _zones[i];
        return null;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var r = Hit(e.Location);
        if (r != null) RegionClicked?.Invoke(r.Id);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var r = Hit(e.Location);
        if (r != _hover)
        {
            _hover = r;
            Cursor = r != null ? Cursors.Hand : Cursors.Default;
            _tip.SetToolTip(this, r == null ? ""
                : r.Label + (_detail.TryGetValue(r.Id, out var d) ? "  " + d : ""));
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != null) { _hover = null; Invalidate(); }
    }

    // ── painting ─────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        float s = ScaleF;
        var save = g.Save();
        g.TranslateTransform((Width - _content.Width * s) / 2f - _content.X * s,
                             (Height - _content.Height * s) / 2f - _content.Y * s);
        g.ScaleTransform(s, s);

        // shoulders/triggers first — the body fill hides their bottom edge
        foreach (var r in _zones)
            if (r.Id is "LT" or "RT" or "LB" or "RB") DrawRegion(g, r);

        using (var bodyFill = new SolidBrush(Color.White))
        using (var bodyPen = new Pen(Color.FromArgb(70, 74, 80), 4f))
        {
            g.FillPath(bodyFill, _body!);
            g.DrawPath(bodyPen, _body!);
        }

        // D-pad centre decoration
        using (var dpFill = new SolidBrush(Color.FromArgb(245, 246, 248)))
        using (var dpPen = new Pen(Color.FromArgb(70, 74, 80), 2f))
        {
            g.FillRectangle(dpFill, 137, 210, 26, 26);
            g.DrawRectangle(dpPen, 137, 210, 26, 26);
        }

        foreach (var r in _zones)
            if (r.Id is not ("LT" or "RT" or "LB" or "RB")) DrawRegion(g, r);

        g.Restore(save);
    }

    // fonts are World-unit so they scale with the canvas transform; cached
    // per size (a handful of distinct sizes exist)
    Font FontFor(float size)
    {
        if (!_fonts.TryGetValue(size, out var f))
            _fonts[size] = f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.World);
        return f;
    }

    void DrawRegion(Graphics g, Zone r)
    {
        var st = _state.TryGetValue(r.Id, out var s) ? s : RegionState.Default;
        bool selected = _selected == r.Id;

        Color fill = st switch
        {
            RegionState.Remapped => Color.FromArgb(188, 214, 250),
            RegionState.Blocked  => Color.FromArgb(250, 212, 212),
            RegionState.Conflict => Color.FromArgb(255, 228, 196),
            _ => _hover == r ? Color.FromArgb(225, 236, 248) : Color.FromArgb(245, 246, 248),
        };
        Color outline = selected ? Color.FromArgb(0, 120, 255) : st switch
        {
            RegionState.Remapped => Color.FromArgb(30, 100, 200),
            RegionState.Blocked  => Color.FromArgb(205, 50, 50),
            RegionState.Conflict => Color.FromArgb(235, 130, 20),
            _ => Color.FromArgb(70, 74, 80),
        };
        float ow = selected ? 4.5f : 2.2f;

        using var fb = new SolidBrush(fill);
        using var op = new Pen(outline, ow);

        switch (r.Kind)
        {
            case RegionKind.Circle:
                g.FillEllipse(fb, r.Bounds);
                g.DrawEllipse(op, r.Bounds);
                break;
            case RegionKind.Ring:
            {
                g.FillEllipse(fb, r.Bounds);
                g.DrawEllipse(op, r.Bounds);
                float cx = r.Bounds.X + r.Bounds.Width / 2f, cy = r.Bounds.Y + r.Bounds.Height / 2f;
                g.DrawEllipse(Pens.Gray, cx - r.Inner, cy - r.Inner, r.Inner * 2, r.Inner * 2);
                break;
            }
            default:
            {
                using var path = Rounded(r.Bounds, 6f);
                g.FillPath(fb, path);
                g.DrawPath(op, path);
                break;
            }
        }

        if (r.Text != null)
        {
            using var tb = new SolidBrush(Color.FromArgb(40, 42, 46));
            var f = FontFor(r.TextSize);
            // exact optical centring: rasterise the glyphs to a path, measure
            // the tight bounding box, and place its centre on the zone centre.
            // DrawString centres the line *box*, which sits differently per
            // glyph (descenders, arrows, cap height) — the path box does not lie.
            using var gp = new GraphicsPath();
            gp.AddString(r.Text, f.FontFamily, (int)f.Style, f.Size,
                         Point.Empty, StringFormat.GenericTypographic);
            var gb = gp.GetBounds();
            var gsave = g.Save();
            g.TranslateTransform(r.Bounds.X + r.Bounds.Width / 2f - (gb.X + gb.Width / 2f),
                                 r.Bounds.Y + r.Bounds.Height / 2f - (gb.Y + gb.Height / 2f));
            g.FillPath(tb, gp);
            g.Restore(gsave);
        }
    }

    static GraphicsPath Rounded(RectangleF b, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(b.X, b.Y, d, d, 180, 90);
        p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
        p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
        p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
