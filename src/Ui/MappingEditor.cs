using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Nefarius.ViGEm.Client.Targets.Xbox360;

// ─── Mapping editor ───────────────────────────────────────────────────────────
// Dropdown-only GUI over mapping.ini. Every value is picked from a dropdown
// that contains exactly the legal targets for that row, so the file this editor
// writes cannot contain the mistakes the text parser has to reject (unknown
// names, numeric values, trigger/stick mixes). The INI stays the storage
// format: the tray's FileSystemWatcher picks up saves automatically, and raw
// hand-editing remains available from the menu.
//
// Layout: gamepad canvas (selector view — click a control, the matching
// dropdown opens; state colors mirror the config live) beside two list columns
// — BUTTONS left, TRIGGERS + STICKS right. Save writes without closing; every
// close path prompts Save/Discard/Cancel while any pad differs from the
// baseline captured at open / last save.

sealed class MappingEditor : Form
{
    // ── editable model, one PadDoc per pad ──────────────────────────────────
    // Mirrors what mapping.ini would contain; "default" = absent from all
    // three dicts = passthrough line omitted from the file.
    sealed class PadDoc
    {
        public readonly Dictionary<Xbox360Button, Xbox360Button> Btn = new();     // remap / block (Guide = none)
        public readonly Dictionary<Xbox360Button, bool>          BtnTrig = new(); // true = LT, false = RT
        public readonly Dictionary<AnalogSource, AnalogEntry>    Analog = new();

        public PadDoc Clone()
        {
            var d = new PadDoc();
            foreach (var kv in Btn)     d.Btn[kv.Key]     = kv.Value;
            foreach (var kv in BtnTrig) d.BtnTrig[kv.Key] = kv.Value;
            foreach (var kv in Analog)  d.Analog[kv.Key]  = kv.Value;
            return d;
        }

        // value equality over the three dicts (AnalogEntry is a struct, so the
        // default comparer compares Kind + Button field-wise)
        public bool SameContent(PadDoc o) =>
            DictEquals(Btn, o.Btn) && DictEquals(BtnTrig, o.BtnTrig) && DictEquals(Analog, o.Analog);

        static bool DictEquals<K, V>(Dictionary<K, V> a, Dictionary<K, V> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kv in a)
                if (!b.TryGetValue(kv.Key, out var v) || !EqualityComparer<V>.Default.Equals(kv.Value, v))
                    return false;
            return true;
        }
    }

    // combo entry: display text + payload (null payload = "default")
    sealed class Opt
    {
        public readonly string  Text;
        public readonly object? Payload; // Xbox360Button | "none" | "swap" | "LT" | "RT"
        public Opt(string text, object? payload) { Text = text; Payload = payload; }
        public override string ToString() => Text;
    }
    static readonly Opt DefaultOpt = new Opt("default", null);
    static readonly Opt NoneOpt    = new Opt("none", "none");

    // display order for digital buttons — matches the mapping.ini template
    static readonly Xbox360Button[] DigitalOrder =
    {
        Xbox360Button.A, Xbox360Button.B, Xbox360Button.X, Xbox360Button.Y,
        Xbox360Button.LeftShoulder, Xbox360Button.RightShoulder,
        Xbox360Button.LeftThumb, Xbox360Button.RightThumb,
        Xbox360Button.Start, Xbox360Button.Back,
        Xbox360Button.Up, Xbox360Button.Down, Xbox360Button.Left, Xbox360Button.Right,
    };

    // analog rows in display order; counterpart = the swap partner name
    static readonly (AnalogSource src, string name, string counterpart)[] AnalogRows =
    {
        (AnalogSource.LT,     "LT",     "RT"),
        (AnalogSource.RT,     "RT",     "LT"),
        (AnalogSource.LStick, "LStick", "RStick"),
        (AnalogSource.RStick, "RStick", "LStick"),
    };

    // ── dialog state ────────────────────────────────────────────────────────
    sealed class PadView
    {
        public readonly ComboBox[] Digital = new ComboBox[DigitalOrder.Length];
        public readonly ComboBox[] Analog  = new ComboBox[AnalogRows.Length];
        // analog source labels — the SPLIT-degrades-swap warning is appended to
        // the label text (a separate column misaligned with the dropdown popup)
        public readonly Label[]    AnalogLabel = new Label[AnalogRows.Length];
    }

    readonly PadDoc[]  _docs  = { new PadDoc(), new PadDoc() };
    readonly PadDoc[]  _baseline = { new PadDoc(), new PadDoc() }; // as of open / last save
    readonly PadView[] _views = { new PadView(), new PadView() };
    readonly TabControl _tabs = new();
    readonly Label     _warning = new();
    readonly ToolTip   _tips = new();
    readonly GamepadCanvas _canvas = new();
    readonly bool[] _padDirty = new bool[2]; // per-pad: drives the "Pad N *" caption
    bool _loading, _dirty;

    public static void Open()
    {
        MappingConfig.EnsureExists();

        // The editor loads through LoadStrict — on a broken file it would show
        // all-defaults, and one Save would silently destroy the user's
        // work-in-progress. Refuse instead and hand over to the raw editor,
        // where the file can be fixed in place.
        string? error = null;
        try { MappingConfig.LoadStrict(); }
        catch (MappingParseException ex) { error = ex.Message; }
        if (error != null)
        {
            var r = MessageBox.Show(
                "mapping.ini has errors, so the editor can't open it:\n" + error +
                "\n\nOpen the file as raw text to fix it?",
                "ViPadLinker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes)
            {
                try { MappingConfig.OpenInShellEditor(); }
                catch (Exception ex) { Log.Exception("MappingEditor raw open", ex); }
            }
            return;
        }

        using var editor = new MappingEditor();
        // no owner window exists — the app is tray-only; TopMost keeps the
        // dialog findable above whatever the user was doing
        editor.ShowDialog();
    }

    MappingEditor()
    {
        Text            = "ViPadLinker - mapping editor";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        ClientSize      = new Size(960, 560);
        MinimumSize     = new Size(860, 460);
        TopMost         = true;   // opened from the tray — keep it findable
        Font            = SystemFonts.MessageBoxFont;

        _warning.Visible     = false;
        _warning.Dock        = DockStyle.Top;
        _warning.AutoSize    = true;
        _warning.ForeColor   = Color.Firebrick;
        _warning.Padding     = new Padding(10, 6, 10, 4);

        _tabs.TabPages.Add(BuildPadTab(0, "Pad 1"));
        _tabs.TabPages.Add(BuildPadTab(1, "Pad 2"));

        // canvas = selector view of the ACTIVE pad: clicking art focuses the
        // matching dropdown; focusing a dropdown highlights the art; tab
        // switch re-pushes the other pad's state
        _canvas.Dock = DockStyle.Fill;
        _canvas.RegionClicked += OnCanvasRegionClicked;
        _tabs.SelectedIndexChanged += (_, __) => PushCanvasState(_tabs.SelectedIndex);

        // SplitContainer, NOT canvas-Dock-Left + tabs-Dock-Fill: with Left
        // added before Fill the tabs ended up laid out UNDER the canvas (the
        // dropdowns popped at the wrong edge). Side-by-side panels are
        // explicit here, and the splitter is draggable.
        var split = new SplitContainer
        {
            Dock          = DockStyle.Fill,
            FixedPanel    = FixedPanel.Panel1,
            SplitterWidth = 6,
        };
        split.Panel1.Controls.Add(_canvas);
        _tabs.Dock = DockStyle.Fill;
        split.Panel2.Padding = new Padding(4, 0, 0, 0);
        split.Panel2.Controls.Add(_tabs);

        // Save deliberately does NOT close — keep tweaking after a save.
        // Close calls Close() explicitly instead of carrying a DialogResult:
        // in a modal dialog, DialogResult buttons (and Esc via CancelButton)
        // end the modal loop WITHOUT FormClosing, which silently skipped the
        // unsaved-changes guard. Close() routes every path through it.
        var saveBtn   = new Button { Text = "Save",              AutoSize = true };
        var cancelBtn = new Button { Text = "Close",             AutoSize = true };
        cancelBtn.Click += (_, __) => Close();
        var resetBtn  = new Button { Text = "Restore defaults",  AutoSize = true };
        _tips.SetToolTip(resetBtn, "Clears the current pad · Ctrl+click clears BOTH pads");
        saveBtn.Click  += (_, __) => Save();
        // mirrors the tray menu convention: plain = normal action, Ctrl = the
        // bigger hammer (reset BOTH pads, not just the visible one)
        resetBtn.Click += (_, __) =>
        {
            if ((Control.ModifierKeys & Keys.Control) != 0)
            { ResetPad(0); ResetPad(1); }
            else ResetPad(_tabs.SelectedIndex);
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(8, 8, 10, 4),
        };
        buttons.Controls.AddRange(new Control[] { cancelBtn, saveBtn, resetBtn });

        // dock layout runs front-to-back of the z-order: bottom/top first,
        // the Fill split container last so it gets what is left
        Controls.Add(buttons);
        Controls.Add(_warning);
        Controls.Add(split);
        split.SplitterDistance = 430; // canvas column
        // On load (layout settled), fit the canvas column to the art's aspect
        // ratio — the gamepad fills the panel instead of floating in a square
        Load += (_, __) =>
        {
            int want = (int)(_canvas.ContentAspect * split.ClientSize.Height);
            // keep at least ~470px for the two packed list columns
            split.SplitterDistance = Math.Max(280, Math.Min(want, Width - 470));
        };
        CancelButton = cancelBtn;
        // the mode can change from the tray menu while this dialog is open —
        // refresh the SPLIT warnings whenever the editor regains focus
        Activated += (_, __) => UpdateSplitWarnings();

        LoadModel();
        // Every user close path lands here: X, Esc (CancelButton → Click →
        // Close()), Close — FormClosing fires for all of them, so the guard
        // is in one place.
        FormClosing += (_, e) =>
        {
            if (!_dirty || e.CloseReason != CloseReason.UserClosing) return;
            var r = MessageBox.Show(this,
                "You have unsaved changes.\n\nSave them before closing?",
                "ViPadLinker", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel)
                e.Cancel = true;                                  // stay in the editor
            else if (r == DialogResult.Yes && !Save())
                e.Cancel = true;                                  // failed save keeps the form
        };
    }

    // ── UI construction ─────────────────────────────────────────────────────

    TabPage BuildPadTab(int pad, string title)
    {
        var page = new TabPage(title) { AutoScroll = true, Padding = new Padding(10, 6, 10, 10) };
        // NOT docked: docked children are excluded from the AutoScroll range
        // calculation — an AutoSize table at 0,0 makes the tab scroll to its
        // real bottom (docking it cut off the last rows with no scrollbar).
        // Two columns: digital buttons left; analog right, split into TRIGGERS
        // (LT/RT) and STICKS (LStick/RStick) — different kinds of controls,
        // one shared "TRIGGERS" header over all four was wrong.
        var outer = new TableLayoutPanel
        {
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount  = 2,
        };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var btnTable = MakeRowTable();
        btnTable.Controls.Add(Header("BUTTONS"), 0, 0);
        btnTable.SetColumnSpan(btnTable.Controls[0], 2);
        for (int i = 0; i < DigitalOrder.Length; i++)
        {
            var src = DigitalOrder[i];
            var (cb, _) = AddRow(btnTable, i + 1, ButtonNames.Name(src), DigitalOpts(),
                box => OnDigitalChanged(pad, src, box));
            _views[pad].Digital[i] = cb;
            string id = ButtonNames.Name(src);
            cb.GotFocus += (_, __) => _canvas.SetSelected(id);
        }

        var anaTable = MakeRowTable();
        anaTable.Margin = new Padding(20, 0, 0, 0);
        anaTable.Controls.Add(Header("TRIGGERS"), 0, 0);
        anaTable.SetColumnSpan(anaTable.Controls[0], 2);
        for (int i = 0; i < AnalogRows.Length; i++)
        {
            var (src, name, counterpart) = AnalogRows[i];
            bool isTrigger = src == AnalogSource.LT || src == AnalogSource.RT;
            if (i == 2) // first stick row — section header between the groups
            {
                anaTable.Controls.Add(Header("STICKS"), 0, 3);
                anaTable.SetColumnSpan(anaTable.Controls[anaTable.Controls.Count - 1], 2);
            }
            var opts = isTrigger ? TriggerOpts(counterpart) : StickOpts(counterpart);
            // rows: 1=LT 2=RT 3=STICKS header 4=LStick 5=RStick
            var (cb, lbl) = AddRow(anaTable, isTrigger ? i + 1 : i + 2, name, opts,
                box => OnAnalogChanged(pad, src, box));
            _views[pad].Analog[i] = cb;
            _views[pad].AnalogLabel[i] = lbl;
            string anaId = name;
            cb.GotFocus += (_, __) => _canvas.SetSelected(anaId);
            _tips.SetToolTip(cb, isTrigger
                ? "swap = LT↔RT · none = block · button = trigger press fires that button"
                : "swap = LStick↔RStick · none = block");
        }

        outer.Controls.Add(btnTable, 0, 0);
        outer.Controls.Add(anaTable, 1, 0);
        page.Controls.Add(outer);
        // Deterministic scroll range: AutoScroll does not reliably derive the
        // range from an AutoSize TableLayoutPanel, which cut RStick off with
        // no scrollbar. Rows are fixed 30px, so the height is known — the
        // buttons column is the tall one now (14 rows + header + padding).
        page.AutoScrollMinSize = new Size(0, DigitalOrder.Length * 30 + 34 + 40);
        return page;
    }

    // source label | dropdown — used by both the buttons and analog columns
    static TableLayoutPanel MakeRowTable()
    {
        // AutoSize columns: an absolute label column left dead space between
        // the (short) names and the dropdowns — grow-to-content packs them
        var t = new TableLayoutPanel
        {
            ColumnCount  = 2,
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding      = new Padding(0, 0, 0, 8),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    static Label Header(string text) => new Label
    {
        Text   = text,
        Font   = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
        AutoSize = true,
        Margin = new Padding(3, 10, 3, 2),
    };

    // returns the row's dropdown AND its source label — the analog rows keep
    // the label so the SPLIT warning can be appended to the source text
    (ComboBox cb, Label label) AddRow(TableLayoutPanel t, int row, string source, IEnumerable<Opt> opts, Action<ComboBox> onChange)
    {
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        var lbl = new Label
        {
            Text   = source,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 7, 1, 3),
        };
        t.Controls.Add(lbl, 0, row);

        var cb = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width         = 140,
            Anchor        = AnchorStyles.Left,
        };
        foreach (var o in opts) cb.Items.Add(o);
        cb.SelectedIndexChanged += (_, __) => { if (!_loading) onChange(cb); };
        t.Controls.Add(cb, 1, row);
        return (cb, lbl);
    }

    // dropdown contents per row kind — the prevention mechanism: an illegal
    // combination is not offered, so it cannot be chosen
    static IEnumerable<Opt> DigitalOpts()
    {
        yield return DefaultOpt;
        foreach (var b in DigitalOrder) yield return new Opt(ButtonNames.Name(b), b);
        yield return NoneOpt;
        yield return new Opt("LT (trigger)", "LT");
        yield return new Opt("RT (trigger)", "RT");
    }

    static IEnumerable<Opt> TriggerOpts(string counterpart)
    {
        yield return DefaultOpt;
        yield return NoneOpt;
        yield return new Opt("swap " + counterpart, "swap");
        foreach (var b in DigitalOrder) yield return new Opt(ButtonNames.Name(b) + " (button)", b);
    }

    static IEnumerable<Opt> StickOpts(string counterpart)
    {
        yield return DefaultOpt;
        yield return NoneOpt;
        yield return new Opt("swap " + counterpart, "swap");
    }

    // ── model ⇄ UI ──────────────────────────────────────────────────────────

    void LoadModel()
    {
        _loading = true;
        string? warn = null;
        try
        {
            var (m1, m2, a1, a2) = MappingConfig.LoadStrict();
            FillPad(_docs[0], m1, a1);
            FillPad(_docs[1], m2, a2);
        }
        catch (MappingParseException ex)
        {
            // the tray keeps the old mappings in memory in this situation;
            // the editor starts clean and says so
            warn = "Current mapping.ini is invalid (" + ex.Message + ") - showing defaults.";
        }

        for (int pad = 0; pad < 2; pad++)
        {
            var doc = _docs[pad];
            for (int i = 0; i < DigitalOrder.Length; i++)
            {
                var src = DigitalOrder[i];
                object? payload = null;
                if (doc.BtnTrig.TryGetValue(src, out var trg)) payload = trg ? "LT" : "RT";
                else if (doc.Btn.TryGetValue(src, out var dst))
                    payload = dst == Xbox360Button.Guide ? "none" : (object)dst;
                SelectOpt(_views[pad].Digital[i], payload);
            }
            for (int i = 0; i < AnalogRows.Length; i++)
            {
                object? payload = doc.Analog.TryGetValue(AnalogRows[i].src, out var e)
                    ? e.Kind == AnalogTargetKind.Block ? "none"
                    : e.Kind == AnalogTargetKind.Swap  ? "swap"
                    : (object)e.Button
                    : null;
                SelectOpt(_views[pad].Analog[i], payload);
            }
        }

        _loading = false;
        for (int pad = 0; pad < 2; pad++) UpdateSwapPairing(pad);
        RefreshBaselines(); // what the user sees now is the "saved" state
        if (warn != null) { _warning.Text = warn; _warning.Visible = true; }
        RecomputeConflicts();
        // SelectedIndex is still -1 here (handle not created yet) — the first
        // tab is what the user will see, so paint that pad
        PushCanvasState(Math.Max(_tabs.SelectedIndex, 0));
    }
    // (LoadModel's catch stays as a safety net — Open() refuses broken files,
    //  so reaching it means the file broke in the instant between check and load)

    static void FillPad(PadDoc doc, ButtonMapping m, AnalogMapping a)
    {
        foreach (var kv in m.Remap)           doc.Btn[kv.Key] = kv.Value;
        foreach (var kv in a.ButtonToTrigger) doc.BtnTrig[kv.Key] = kv.Value;
        foreach (var kv in a.AnalogMap)       doc.Analog[kv.Key] = kv.Value;
    }

    static void SelectOpt(ComboBox cb, object? payload)
    {
        foreach (Opt o in cb.Items)
        {
            bool same =
                payload == null && o.Payload == null ||
                payload is Xbox360Button b && o.Payload is Xbox360Button ob && b == ob ||
                payload is string s && o.Payload is string os && s == os;
            if (same) { cb.SelectedItem = o; return; }
        }
        cb.SelectedIndex = 0; // should not happen — opts cover every payload
    }

    void OnDigitalChanged(int pad, Xbox360Button src, ComboBox cb)
    {
        var doc = _docs[pad];
        doc.Btn.Remove(src);
        doc.BtnTrig.Remove(src);
        switch (cb.SelectedItem)
        {
            case Opt { Payload: Xbox360Button b }:  doc.Btn[src] = b; break;
            case Opt { Payload: "none" }:           doc.Btn[src] = Xbox360Button.Guide; break;
            case Opt { Payload: "LT" }:             doc.BtnTrig[src] = true; break;
            case Opt { Payload: "RT" }:             doc.BtnTrig[src] = false; break;
            // default → stays absent
        }
        RefreshDirty();
        RecomputeConflicts();
        PushCanvasState(pad);
    }

    void OnAnalogChanged(int pad, AnalogSource src, ComboBox cb)
    {
        var doc = _docs[pad];
        doc.Analog.Remove(src);
        switch (cb.SelectedItem)
        {
            case Opt { Payload: "none" }:
                doc.Analog[src] = new AnalogEntry { Kind = AnalogTargetKind.Block }; break;
            case Opt { Payload: "swap" }:
                doc.Analog[src] = new AnalogEntry { Kind = AnalogTargetKind.Swap }; break;
            case Opt { Payload: Xbox360Button b }:
                doc.Analog[src] = new AnalogEntry { Kind = AnalogTargetKind.Button, Button = b }; break;
        }
        UpdateSwapPairing(pad); // may clear the counterpart — compare after
        RefreshDirty();
        RecomputeConflicts();
        PushCanvasState(pad);
    }

    // ── canvas sync ──────────────────────────────────────────────────────

    // click on the art → focus + open the matching dropdown (the GotFocus
    // handler paints the selection back onto the canvas)
    void OnCanvasRegionClicked(string id)
    {
        int pad = _tabs.SelectedIndex;
        if (pad < 0 || pad >= _views.Length) return; // -1 before the handle exists
        ComboBox? cb = null;
        for (int i = 0; i < DigitalOrder.Length; i++)
            if (ButtonNames.Name(DigitalOrder[i]) == id) { cb = _views[pad].Digital[i]; break; }
        if (cb == null)
            for (int i = 0; i < AnalogRows.Length; i++)
                if (AnalogRows[i].name == id) { cb = _views[pad].Analog[i]; break; }
        if (cb == null) return;
        cb.Focus();
        cb.DroppedDown = true;
    }

    // push the pad's config onto the art: blue = remapped, red = blocked,
    // orange = conflict, plus a tooltip detail like "→ B" / "swap RT"
    void PushCanvasState(int pad)
    {
        // SelectedIndexChanged can fire with -1 (handle not created yet, or
        // pages in transition) — nothing to paint for a pad that isn't shown
        if (pad < 0 || pad >= _docs.Length) return;
        var doc = _docs[pad];
        var destCount = DestCounts(doc);

        foreach (var src in DigitalOrder)
        {
            string id = ButtonNames.Name(src);
            var st = RegionState.Default; string detail = "";
            if (doc.BtnTrig.TryGetValue(src, out var trg))
                (st, detail) = (RegionState.Remapped, "→ " + (trg ? "LT" : "RT"));
            else if (doc.Btn.TryGetValue(src, out var dst))
            {
                if (dst == Xbox360Button.Guide) (st, detail) = (RegionState.Blocked, "blocked");
                else if (dst != src) (st, detail) = (RegionState.Remapped, "→ " + ButtonNames.Name(dst));
            }
            if (st != RegionState.Blocked && IsConflicting(doc, destCount, id))
                (st, detail) = (RegionState.Conflict, "conflict " + detail);
            _canvas.SetState(id, st, detail);
        }
        foreach (var (src, name, counterpart) in AnalogRows)
        {
            var st = RegionState.Default; string detail = "";
            if (doc.Analog.TryGetValue(src, out var e))
            {
                if (e.Kind == AnalogTargetKind.Block) (st, detail) = (RegionState.Blocked, "blocked");
                else if (e.Kind == AnalogTargetKind.Swap) (st, detail) = (RegionState.Remapped, "swap " + counterpart);
                else (st, detail) = (RegionState.Remapped, "→ " + ButtonNames.Name(e.Button));
            }
            _canvas.SetState(name, st, detail);
        }
        _canvas.Invalidate();
    }

    // A swap is ONE mapping covering both sides (LT↔RT, LStick↔RStick). Once a
    // row is set to swap, its counterpart has nothing left to choose: disable
    // it and clear any previous value (the swap wins) until the swap is gone.
    // AnalogRows pairs are adjacent, so partner index = i ^ 1.
    void UpdateSwapPairing(int pad)
    {
        var doc = _docs[pad];
        for (int i = 0; i < AnalogRows.Length; i++)
        {
            var cb = _views[pad].Analog[i];
            bool counterpartSwapped =
                doc.Analog.TryGetValue(AnalogRows[i ^ 1].src, out var e) &&
                e.Kind == AnalogTargetKind.Swap;
            if (!counterpartSwapped) { cb.Enabled = true; continue; }
            doc.Analog.Remove(AnalogRows[i].src);
            _loading = true;
            SelectOpt(cb, null);
            _loading = false;
            cb.Enabled = false;
        }
    }

    void ResetPad(int pad)
    {
        var doc = _docs[pad];
        doc.Btn.Clear();
        doc.BtnTrig.Clear();
        doc.Analog.Clear();
        _loading = true;
        foreach (var cb in _views[pad].Digital) cb.SelectedIndex = 0;
        foreach (var cb in _views[pad].Analog)  cb.SelectedIndex = 0;
        _loading = false;
        UpdateSwapPairing(pad);
        RefreshDirty();
        RecomputeConflicts();
        PushCanvasState(pad);
    }

    // Dirty is content-based: a pad counts as changed only when it differs
    // from the baseline captured at open / last save, so undoing edits by
    // hand clears the flag (and resetting an already-default pad never marks
    // it dirty). The baseline is "as of open", not "as on disk right now".
    void RefreshDirty()
    {
        for (int pad = 0; pad < 2; pad++)
            _padDirty[pad] = !_docs[pad].SameContent(_baseline[pad]);
        _dirty = _padDirty[0] || _padDirty[1];
        UpdateTabTitles();
    }

    void RefreshBaselines()
    {
        for (int pad = 0; pad < 2; pad++) _baseline[pad] = _docs[pad].Clone();
    }

    // unsaved edits are shared across pads in memory, but the star tells the
    // user which tab actually changed — Save() writes both pads at once
    void UpdateTabTitles()
    {
        for (int i = 0; i < _tabs.TabPages.Count; i++)
            _tabs.TabPages[i].Text = "Pad " + (i + 1) + (_padDirty[i] ? " *" : "");
    }

    // ── conflict highlighting ───────────────────────────────────────────────
    // Two sources on the same pad aiming at one destination (button or
    // trigger) is legal in the file but almost always a mistake — flag the
    // rows while editing instead of leaving it to be discovered in-game.
    // destination → how many sources aim at it (blocked and identity are not
    // destinations). Shared by the list tinting and the canvas coloring.
    static Dictionary<string, int> DestCounts(PadDoc doc)
    {
        var destCount = new Dictionary<string, int>();
        void AddDest(string d) => destCount[d] = destCount.TryGetValue(d, out var n) ? n + 1 : 1;

        foreach (var kv in doc.Btn)
            if (kv.Value != Xbox360Button.Guide && kv.Value != kv.Key)
                AddDest(ButtonNames.Name(kv.Value));
        foreach (var kv in doc.BtnTrig)
            AddDest(kv.Value ? "LT" : "RT");
        foreach (var kv in doc.Analog)
            if (kv.Value.Kind == AnalogTargetKind.Button)
                AddDest(ButtonNames.Name(kv.Value.Button));
        return destCount;
    }

    // does the source named id aim (button→button or button→trigger) at a
    // destination more than one source claims?
    static bool IsConflicting(PadDoc doc, Dictionary<string, int> destCount, string id)
    {
        string? dest = null;
        foreach (var src in DigitalOrder)
            if (ButtonNames.Name(src) == id)
            {
                dest =
                    doc.BtnTrig.TryGetValue(src, out var trg) ? (trg ? "LT" : "RT") :
                    doc.Btn.TryGetValue(src, out var dst) && dst != Xbox360Button.Guide && dst != src
                        ? ButtonNames.Name(dst) : null;
                break;
            }
        if (dest == null)
            for (int i = 0; i < AnalogRows.Length; i++)
                if (AnalogRows[i].name == id &&
                    doc.Analog.TryGetValue(AnalogRows[i].src, out var e) &&
                    e.Kind == AnalogTargetKind.Button)
                { dest = ButtonNames.Name(e.Button); break; }
        return dest != null && destCount.TryGetValue(dest, out var n) && n > 1;
    }

    void RecomputeConflicts()
    {
        for (int pad = 0; pad < 2; pad++)
        {
            var doc = _docs[pad];
            for (int i = 0; i < DigitalOrder.Length; i++)
                _views[pad].Digital[i].BackColor =
                    IsConflicting(doc, DestCounts(doc), ButtonNames.Name(DigitalOrder[i]))
                        ? Color.MistyRose : SystemColors.Window;
            for (int i = 0; i < AnalogRows.Length; i++)
                _views[pad].Analog[i].BackColor =
                    IsConflicting(doc, DestCounts(doc), AnalogRows[i].name)
                        ? Color.MistyRose : SystemColors.Window;
        }
        UpdateSplitWarnings();
    }

    // a swap row is only degraded while a link runs in SPLIT — re-evaluated on
    // every edit and whenever the form is re-activated, because the mode can
    // be toggled from the tray menu while the editor is open. The warning is
    // appended to the row's own source label ("LT ⚠") so it can never drift
    // out of alignment with the dropdown the way a separate column did.
    void UpdateSplitWarnings()
    {
        bool split = TrayApp.LinkActive && TrayApp.LinkModeSplit;
        for (int pad = 0; pad < 2; pad++)
            for (int i = 0; i < AnalogRows.Length; i++)
            {
                var lbl = _views[pad].AnalogLabel[i];
                bool warn = split &&
                    _docs[pad].Analog.TryGetValue(AnalogRows[i].src, out var e) &&
                    e.Kind == AnalogTargetKind.Swap;
                lbl.Text      = warn ? AnalogRows[i].name + "  \u26A0" : AnalogRows[i].name;
                lbl.ForeColor = warn ? Color.Firebrick : SystemColors.ControlText;
                _tips.SetToolTip(lbl, warn
                    ? "SPLIT mode is active: a swap needs both sides on one pad, so here it acts as block (none)."
                    : null);
            }
    }

    // ── save ────────────────────────────────────────────────────────────────

    string BuildIni()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ViPadLinker button mapping - written by the mapping editor");
        sb.AppendLine("# (tray menu: Ctrl+click 'Edit mappings…' to edit as raw text)");
        sb.AppendLine("# (Ctrl+Shift+click 'Edit mappings…' resets to the default template)");
        sb.AppendLine("#");
        sb.AppendLine("# Digital buttons: A B X Y LB RB LS RS Start Back Up Down Left Right");
        sb.AppendLine("# Analog sources:  LT RT LStick RStick");
        sb.AppendLine("#");
        sb.AppendLine("# NOTE: in SPLIT mode, analog swap acts as block (= none)");
        for (int pad = 0; pad < 2; pad++)
        {
            sb.AppendLine();
            sb.AppendLine($"[Pad{pad + 1}]");
            var doc = _docs[pad];
            foreach (var src in DigitalOrder)
            {
                if (doc.BtnTrig.TryGetValue(src, out var trg))
                    sb.AppendLine($"{ButtonNames.Name(src)} = {(trg ? "LT" : "RT")}");
                else if (doc.Btn.TryGetValue(src, out var dst))
                    sb.AppendLine($"{ButtonNames.Name(src)} = {(dst == Xbox360Button.Guide ? "none" : ButtonNames.Name(dst))}");
            }
            foreach (var (src, name, counterpart) in AnalogRows)
            {
                if (!doc.Analog.TryGetValue(src, out var e)) continue;
                string dst = e.Kind switch
                {
                    AnalogTargetKind.Block => "none",
                    AnalogTargetKind.Swap  => counterpart,
                    _                      => ButtonNames.Name(e.Button),
                };
                sb.AppendLine($"{name} = {dst}");
            }
        }
        return sb.ToString();
    }

    // true = file written; false = rejected or I/O failed (dialog shown, old
    // file untouched). Callers closing the form use this to stay open.
    bool Save()
    {
        var path = MappingConfig.ConfigFilePath;
        var text = BuildIni();
        try
        {
            // validate in memory FIRST — the UI cannot build an invalid file,
            // but if the parser ever disagrees the old file is left untouched.
            // No .bak here: the app only backs up before it itself destroys a
            // file (reset, template self-heal); a rejected save destroys nothing.
            MappingConfig.ValidateText(text);

            File.WriteAllText(path, text);
            Log.Info("mapping.ini saved from the mapping editor.");
            // watcher reloads + balloons; form stays open for more edits.
            // One file holds both pads, so both baselines move to saved state.
            RefreshBaselines();
            RefreshDirty();
            return true;
        }
        catch (MappingParseException ex)
        {
            MessageBox.Show(this, "Internal error - generated mapping was rejected, file unchanged:\n" + ex.Message,
                "ViPadLinker", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            Log.Exception("MappingEditor.Save", ex);
            MessageBox.Show(this, "Could not save mapping.ini:\n" + ex.Message,
                "ViPadLinker", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return false;
    }
}
