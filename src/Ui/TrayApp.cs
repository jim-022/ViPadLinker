using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Reflection;
using System.Windows.Forms;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

// ─── Tray app ─────────────────────────────────────────────────────────────────
// The app: a NotifyIcon context menu over the core (Linking/, Mapping/,
// Platform/). Entry point and bootstrap live in src/Program.cs.
// Partial: pure drawing lives in TrayApp.Icons.cs (status icons) and
// TrayApp.Strip.cs (owner-drawn slot row) — this file keeps the app logic
// and menu wiring.

static partial class TrayApp
{
    internal static ViGEmClient?             client;   // created by Program
    static readonly List<IXbox360Controller> viPads = new();
    static LinkSession?                      link;

    // the mapping editor flags swap rows that SPLIT mode degrades to block —
    // read-only view of the live session state (null link = not linked)
    internal static bool LinkActive    => link != null;
    internal static bool LinkModeSplit => link?.Mode == LinkMode.Split;
    static Thread?                           detectThread;

    // physical-pad watcher (holder/idle mode): announces pads the app is NOT
    // using, so multi-pad slot layouts can be verified as they are built
    static Thread?        _padWatcher;
    static volatile bool  _padWatcherRun = true;

    static NotifyIcon?                    _icon;
    static SynchronizationContext?        _sync;

    static ToolStripMenuItem _statusItem      = null!;
    static ToolStripMenuItem _slotStripItem   = null!;
    static ToolStripMenuItem _restoreItem     = null!;
    static ToolStripMenuItem _addHolderItem   = null!;
    static ToolStripMenuItem _removeHolderItem = null!;
    static ToolStripMenuItem _startLinkItem   = null!;
    static ToolStripMenuItem _stopLinkItem    = null!;
    static ToolStripMenuItem _toggleModeItem  = null!;
    static ToolStripMenuItem _editorItem      = null!;
    static ToolStripMenuItem _openLogItem     = null!;

    // mapping lifecycle: mapping.ini is only read at Start Link (strict) and
    // watched while a link runs — outside link mode file edits are inert and
    // nothing is validated. Strict load rejects the whole file on any parse
    // error; the running config (memory) survives and the user fixes the file
    // via the error balloon. .bak is written ONLY right before the app itself
    // destroys the file (reset, template self-heal) — one scoped undo step.
    static System.IO.FileSystemWatcher? _watcher;
    static DateTime _lastReload = DateTime.MinValue;
    static System.Threading.Timer? _reloadTimer;   // rooted so GC keeps it alive
    const int ReloadDebounceMs = 400;
    static string MappingPath => MappingConfig.ConfigFilePath;
    static string BackupPath  => MappingConfig.ConfigFilePath + ".bak";
    // ── Tray UI ───────────────────────────────────────────────────────────────

    internal static void BuildTray()
    {
        _statusItem        = new ToolStripMenuItem("Idle - no pads") { Enabled = false };
        _slotStripItem     = new ToolStripMenuItem("[ ·  |  ·  |  ·  |  · ]")
                             { Enabled = false, ToolTipText = SlotLegend };
        _restoreItem       = new ToolStripMenuItem("Restore backup (mapping.ini.bak)") { Visible = false };
        _addHolderItem     = new ToolStripMenuItem("Add ViPad (hold slot)");
        _removeHolderItem  = new ToolStripMenuItem("Remove ViPad");
        _startLinkItem     = new ToolStripMenuItem("Start Link");
        _stopLinkItem      = new ToolStripMenuItem("Stop Link");
        _toggleModeItem    = new ToolStripMenuItem("Toggle LINKED / SPLIT");
        _editorItem        = new ToolStripMenuItem("Edit mappings…")
                             { ToolTipText = "Ctrl+click: raw text  ·  Ctrl+Shift+click: reset all mappings" };
        _openLogItem       = new ToolStripMenuItem("Open log");
        var quitItem       = new ToolStripMenuItem("Quit");

        _restoreItem.Click      += (_, __) => RestoreBackup();
        _addHolderItem.Click    += (_, __) => AddViPad();
        // Ctrl+click = release every held ViPad in one go. ModifierKeys is the
        // reliable source at Click time — ToolStrip item Click events fire on
        // release and their MouseEventArgs modifier state is not dependable.
        _removeHolderItem.Click += (_, __) =>
            RemoveViPad(removeAll: (Control.ModifierKeys & Keys.Control) != 0);
        _startLinkItem.Click    += (_, __) => StartLink();
        _stopLinkItem.Click     += (_, __) => StopLink();
        _toggleModeItem.Click   += (_, __) => ToggleMode();
        // Ctrl+click = raw text edit, Ctrl+Shift+click = reset all (Alt is
        // unusable here — it puts the menu into mnemonic mode). Reload has no
        // menu entry: the FileSystemWatcher reloads on every save, and
        // Restore backup covers bad-file recovery.
        _editorItem.Click       += (_, __) =>
        {
            var mods = Control.ModifierKeys;
            if ((mods & (Keys.Control | Keys.Shift)) == (Keys.Control | Keys.Shift)) ResetMappings();
            else if ((mods & Keys.Control) != 0) EditMappings();
            else OpenMappingEditor();
        };
        _openLogItem.Click      += (_, __) => OpenLog();
        quitItem.Click          += (_, __) => Quit();

        // net48 has no ToolStripItem.OwnerDraw/DrawItem (that's Core+). The
        // supported hook is a custom ToolStripRenderer: OnRenderItemText hands
        // us the exact text rectangle (already past the check gutter) — we skip
        // the base draw for the strip item and paint cells there instead. The
        // all-spaces Text only reserves width.
        // ShowImageMargin off: no item uses icons, and Windows paints the
        // reserved icon gutter as a gray band — dead space without them.
        var menu = new ContextMenuStrip { Renderer = new StripCellRenderer(), ShowImageMargin = false };

        // rebuild the slots view each time the menu opens — always current,
        // no per-second allocations when the menu is closed
        // refresh everything at the moment the menu becomes visible — replaces
        // the old 1s timer: fresher (no staleness window) and zero wakeups.
        // Menu closes on every action and balloons confirm, so nothing else
        // needs periodic refresh.
        menu.Opening += (_, __) =>
        {
            UpdateStatus();
            UpdateMenuLabels();
        };

        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem, _slotStripItem,
            new ToolStripSeparator(),
            _addHolderItem, _removeHolderItem,
            new ToolStripSeparator(),
            _startLinkItem, _stopLinkItem, _toggleModeItem,
            new ToolStripSeparator(),
            _editorItem, _restoreItem,
            new ToolStripSeparator(),
            _openLogItem,
            new ToolStripSeparator(),
            quitItem
        });

        Icon trayIcon;
        try { trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { trayIcon = SystemIcons.Application; }
        BuildStateIcons(trayIcon);

        _icon = new NotifyIcon
        {
            Icon             = SlotMapIcon(),   // all slots free at startup = bare icon
            Text             = "ViPadLinker - idle",
            Visible          = true,
            ContextMenuStrip = menu
        };
        // Left-click opens the same menu as right-click. Safe now that ShowTrayMenu
        // exists: it routes through NotifyIcon's own ShowContextMenu path (owned
        // popup, no taskbar button, click-away dismiss) instead of menu.Show(),
        // which used to create an unowned dropdown. Right-click is still handled
        // natively by ContextMenuStrip, so the handler must filter to left only.
        _icon.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowTrayMenu();
        };

        // capture the WinForms sync context so background threads
        // (pad detection) can raise balloon tips on the UI thread
        _sync = SynchronizationContext.Current;

        // auto-reload mapping.ini when it changes on disk (editor save).
        // Editors fire Changed multiple times — the debounce window coalesces.
        // Created inactive: mappings only matter while a link runs, so the
        // watcher is switched on by StartLink and off by StopLink.
        try
        {
            _watcher = new System.IO.FileSystemWatcher(
                AppDomain.CurrentDomain.BaseDirectory, "mapping.ini")
            {
                NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.Size | System.IO.NotifyFilters.FileName,
                EnableRaisingEvents = false
            };
            _watcher.Changed  += (_, __) => ScheduleReload();
            _watcher.Created  += (_, __) => ScheduleReload();
            _watcher.Deleted  += (_, __) => ScheduleReload();
            _watcher.Renamed  += (_, __) => ScheduleReload();
        }
        catch (Exception ex) { Log.Exception("FileSystemWatcher init", ex); }

        // clicking an error balloon jumps straight to the broken file — no
        // menu, no Ctrl+click. Only while the file is still broken: an old
        // balloon clicked after the fix must not yank open notepad.
        _icon.BalloonTipClicked += (_, __) =>
        {
            // any balloon click consumes the pending-error intent — an info
            // balloon clicked later must not open the file (the flag would
            // otherwise survive until some unrelated click)
            if (_errorBalloonPending)
            {
                _errorBalloonPending = false;
                try
                {
                    MappingConfig.LoadStrict();
                    Log.Info("Error balloon clicked but mapping.ini now valid - not opening editor.");
                }
                catch (MappingParseException)
                {
                    try { MappingConfig.OpenInShellEditor(); }
                    catch (Exception ex) { Log.Exception("BalloonTipClicked open", ex); }
                }
            }
        };

        UpdateStatus();
        // With an auto-start action the specific balloon ("ViPad on slot 0..." /
        // "ViPad added - N slot(s) held") already says what is running — a second
        // generic one would just stack on top of it.
        if (!Program._startLink && Program._startPads == 0)
            Balloon("Running - click the icon for actions.");

        // physical-pad watcher: announces pads the app is NOT using (holder/idle
        // mode). Paused while a link is active — link mode has its own detection
        // and must not double-report its pads.
        _padWatcher = new Thread(PadWatcherLoop) { IsBackground = true, Name = "PadWatcher" };
        _padWatcher.Start();
    }

    // -- Win32 interop for ShowTrayMenu -------------------------------------------
    // Windows only lets the foreground process (or one it granted rights to) move
    // focus. A tray app sitting in the background is "locked out": its
    // SetForegroundWindow calls silently fail. These four let ShowTrayMenu borrow
    // the foreground thread's input state for the moment the menu opens.

    // Handle of the window the user is currently working in (any process).
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    // Thread that owns a window. Called with IntPtr.Zero for the second arg
    // because only the thread id is needed, not the process id.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    // Native id of the calling thread (not the same as ManagedThreadId, which
    // AttachThreadInput would not accept).
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    // Joins (true) or separates (false) the input queues of two threads. While
    // joined, both share focus/activation state, so our thread counts as part of
    // the foreground and SetForegroundWindow is allowed. Must ALWAYS be detached
    // again: attached queues tie the two apps together, and a hang in either can
    // freeze the other.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    // Opens the tray context menu from code, exactly as if the user right-clicked
    // the icon. Safe to call from ANY thread.
    //
    // Why not menu.Show(Cursor.Position)? That creates the dropdown as an unowned
    // top-level window: it shows up in the taskbar and never closes on click-away.
    // NotifyIcon's private ShowContextMenu() is the same path Windows uses on a real
    // right-click: it calls SetForegroundWindow on the icon's hidden window first,
    // then opens the menu at the cursor. It has been in NotifyIcon since .NET 2.0;
    // being private it is reached via reflection, and the ?. makes the call a
    // no-op instead of a crash if the method ever disappears.
    //
    // Three problems this method solves:
    //  1. Thread: the menu window is created on the calling thread. Called from a
    //     background thread (show-signal waiter, pad watcher, timer) it lands on a
    //     thread with no message loop: invisible, stuck in the taskbar, process
    //     "not responding", and the menu is broken until restart. So hop to the UI
    //     thread first, same pattern as Balloon().
    //  2. Focus: a background app's SetForegroundWindow is blocked by Windows, and
    //     a menu without foreground never receives the click-away that dismisses
    //     it (the user had to click inside it first). Attaching to the foreground
    //     thread's input for the duration of the call lets it succeed.
    //  3. Cleanup: ShowContextMenu returns as soon as the menu is shown (it is not
    //     modal), so the attach lasts only a moment; finally guarantees the detach
    //     even if the reflection call throws.
    internal static void ShowTrayMenu()
    {
        // (1) not on the UI thread -> re-post there and bail
        if (_sync != null && _sync != SynchronizationContext.Current)
        {
            _sync.Post(_ => ShowTrayMenu(), null);
            return;
        }

        if (_icon == null || !_icon.Visible) return;

        // (2) borrow foreground rights. Skip the attach when there is no foreground
        // window (fgThread 0) or it is already ours: attaching a thread to itself fails.
        uint myThread = GetCurrentThreadId();
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        bool attached = fgThread != 0 && fgThread != myThread
                        && AttachThreadInput(myThread, fgThread, true);
        try
        {
            typeof(NotifyIcon)
                .GetMethod("ShowContextMenu",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(_icon, null);
        }
        finally
        {
            // (3) never leave the input queues joined
            if (attached) AttachThreadInput(myThread, fgThread, false);
        }
    }

    // Polls the 4 XInput slots every 500 ms and balloons physical pads appearing/
    // disappearing — the set EXCLUDES our holder ViPads and link pads, so holders
    // never trigger their own balloons. Changes must persist 2 consecutive polls
    // (~1 s) before announcing: wireless pads drop GetState for up to ~50 ms
    // (the phantom drops the link freeze logic defends against), and a single
    // failed poll must not balloon a disconnect.
    static void PadWatcherLoop()
    {
        var known = new HashSet<uint>();
        var appearedStreak = new int[4];
        var goneStreak     = new int[4];
        bool seeded = false;
        bool wasLinked = false;

        while (_padWatcherRun)
        {
            try
            {
                // holder ViPad slots — snapshot under the same lock the UI thread uses
                var holderSlots = new HashSet<uint>();
                lock (viPads)
                    foreach (var pad in viPads)
                        try { holderSlots.Add((uint)pad.UserIndex); } catch { }

                var now = new HashSet<uint>();
                for (uint i = 0; i < 4; i++)
                {
                    if (holderSlots.Contains(i)) continue;   // our ViPad, not "physical"
                    if (link != null && link.PadIndexForSlot(i) >= 0) continue; // link pad
                    if (XInput.GetState(i, out _)) now.Add(i);
                }

                if (link != null)
                {
                    // link mode owns pad announcements — silent re-sync, no balloons
                    foreach (var s in now) { known.Add(s); appearedStreak[s] = 0; goneStreak[s] = 0; }
                    known.RemoveWhere(s => !now.Contains(s));
                    wasLinked = true;
                    Thread.Sleep(500);
                    continue;
                }

                // link just stopped: its pads are still plugged in but now count as
                // physical — re-seed silently instead of bursting connect balloons
                if (wasLinked)
                {
                    known.Clear();
                    foreach (var s in now) known.Add(s);
                    wasLinked = false;
                }

                if (!seeded)   // pads already plugged in at startup never balloon
                {
                    foreach (var s in now) known.Add(s);
                    seeded = true;
                }

                bool changed = false;
                for (uint i = 0; i < 4; i++)
                {
                    bool wasKnown = known.Contains(i);
                    bool isNow    = now.Contains(i);

                    if (isNow && !wasKnown)
                    {
                        goneStreak[i] = 0;
                        if (++appearedStreak[i] >= 2)
                        {
                            known.Add(i); appearedStreak[i] = 0;
                            Sound.Connected();
                            Balloon($"Physical pad connected on slot {SlotLabel.N(i)}");
                            Log.Info($"Pad watcher: physical pad appeared on slot {i}.");
                            changed = true;
                        }
                    }
                    else if (!isNow && wasKnown)
                    {
                        appearedStreak[i] = 0;
                        if (++goneStreak[i] >= 2)
                        {
                            known.Remove(i); goneStreak[i] = 0;
                            Sound.Disconnected();
                            Balloon($"Physical pad disconnected from slot {SlotLabel.N(i)}",
                                    ToolTipIcon.Warning);
                            Log.Info($"Pad watcher: physical pad gone from slot {i}.");
                            changed = true;
                        }
                    }
                    else
                    {
                        appearedStreak[i] = 0; goneStreak[i] = 0;
                    }
                }

                if (changed) _sync?.Post(_ => UpdateStatus(), null);
            }
            catch (Exception ex) { Log.Exception("PadWatcherLoop", ex); }

            Thread.Sleep(500);
        }
    }

    // ── Slot holder ───────────────────────────────────────────────────────────

    internal static void AddViPad()
    {
        if (link != null) { Warn("Not supported in Link mode. Stop Link first."); return; }
        if (XInput.AllSlotsFull) { Warn("No free XInput slots, max is 4."); return; }
        try
        {
            var pad = client!.CreateXbox360Controller();
            pad.Connect();
            lock (viPads) { viPads.Add(pad); }   // pad watcher reads this list
            Log.Info($"Holder added. Total viPads: {viPads.Count}");
            Balloon($"ViPad added - {viPads.Count} slot(s) held");
        }
        catch (Exception ex)
        {
            Log.Exception("AddViPad", ex);
            Warn("Failed to add pad: " + ex.Message);
        }
        UpdateStatus();
    }

    static void RemoveViPad(bool removeAll = false)
    {
        if (link != null) { Warn("Not supported in Link mode. Stop Link first."); return; }
        if (viPads.Count == 0) { Warn("No virtual pads to remove."); return; }

        // detach under the lock (the pad watcher reads the list), release the
        // ViGEm handles outside it — Pad.Release does driver calls
        var doomed = new List<IXbox360Controller>();
        lock (viPads)
        {
            if (removeAll)
            {
                doomed.AddRange(viPads);
                viPads.Clear();
            }
            else
            {
                doomed.Add(viPads[viPads.Count - 1]);
                viPads.RemoveAt(viPads.Count - 1);
            }
        }
        foreach (var pad in doomed) Pad.Release(pad);

        int removed = doomed.Count;
        Log.Info(removed == 1
            ? $"Holder removed. Total viPads: {viPads.Count}"
            : $"All holders removed ({removed}). Total viPads: 0");
        Balloon(removed > 1
            ? $"{removed} ViPads removed - all slots free"
            : viPads.Count == 0
                ? "Last ViPad removed - all slots free"
                : $"ViPad removed - {viPads.Count} slot(s) still held");
        UpdateStatus();
    }

    // ── Link ──────────────────────────────────────────────────────────────────

    internal static void StartLink()
    {
        if (link != null) { Warn("Link already active."); return; }
        if (viPads.Count > 0) { Warn("Release held slots first, then start Link."); return; }
        if (XInput.GetState(0, out _)) { Warn("A pad is already on slot 1 - Link needs it free."); return; }

        // mappings are needed only in link mode — validate here, not at app
        // start. A broken file must not block linking: start pass-through and
        // send the user to the file via the error balloon.
        ButtonMapping m1, m2; AnalogMapping a1, a2;
        string? mapError = null;
        try { (m1, m2, a1, a2) = MappingConfig.LoadStrict(); }
        catch (MappingParseException ex)
        {
            Log.Warn($"mapping.ini rejected at link start: {ex.Message} - starting pass-through.");
            (m1, m2, a1, a2) = MappingConfig.Empty();
            mapError = ex.Message;
        }

        try
        {
            link = new LinkSession(client!, m1, m2, a1, a2);
            // pad connect/disconnect during the link → balloon (Balloon
            // marshals to the UI thread; the event fires on the poll thread)
            link.PadStatusChanged += (padIndex, slot, alive) =>
            {
                Balloon(alive
                    ? $"Pad {padIndex + 1} (slot {SlotLabel.N(slot)}) reconnected."
                    : $"Pad {padIndex + 1} (slot {SlotLabel.N(slot)}) disconnected - inputs frozen.",
                    alive ? ToolTipIcon.Info : ToolTipIcon.Warning);
                // icon/strip must follow the event immediately — no timer to
                // catch it later (event fires on the poll thread)
                _sync?.Post(_ => UpdateStatus(), null);
            };
            Log.Info("LinkSession created. Virtual pad on slot 0.");
        }
        catch (Exception ex)
        {
            Log.Exception("StartLink create session", ex);
            Warn("Failed to create virtual pad: " + ex.Message);
            return;
        }

        // one-shot CLI mode: --link split (session always starts LINKED, toggle once)
        if (Program._startSplit)
        {
            Program._startSplit = false;
            link.ToggleMode();
        }

        // One balloon, not two: Windows shows a single toast at a time with a
        // ~5s minimum, so a start balloon followed by an error balloon means
        // the actionable message can be missed. When the file is broken the
        // error IS the news — lead with it and skip the generic start note.
        if (mapError != null)
            BalloonError($"Link started with NO mappings - mapping.ini has errors:\n{mapError}\n\nClick to fix the file.");
        else
            Balloon(link.Mode == LinkMode.Split
                ? "ViPad on slot 1 (SPLIT mode) - connect Pad 1 now.\nPad 1 = left side, Pad 2 = right side."
                : "ViPad on slot 1 - connect Pad 1 now. Pad 2 joins any time.");

        SetWatcherActive(true); // file edits now live: reload while linked
        var capturedLink = link;
        detectThread = new Thread(() =>
        {
            try { WaitForPads(capturedLink); }
            catch (Exception ex) { Log.Exception("Detection thread", ex); }
        }) { IsBackground = true };
        detectThread.Start();
        UpdateStatus();
    }

    // watches slots 1-3 — finds pad 1 then keeps watching so pad 2
    // can join at ANY time, exits when both are found
    static void WaitForPads(LinkSession session)
    {
        while (session.IsRunning)
        {
            if (session.PadCount == 0 && !session.IsWaiting)
                return; // cancelled via Stop Link

            for (uint i = 1; i < 4; i++)
            {
                if (session.PadIndexForSlot(i) >= 0) continue;
                if (XInput.GetState(i, out _))
                {
                    session.AddPad(i);
                    Sound.Connected();
                    _sync?.Post(_ => UpdateStatus(), null); // icon: waiting → live

                    if (session.PadCount == 1)
                        Balloon($"Pad 1 detected on slot {SlotLabel.N(i)} - link active.\nPad 2 joins automatically when connected.");
                    else
                    {
                        Balloon($"Pad 2 detected on slot {SlotLabel.N(i)} - joined link.");
                        return;
                    }
                    break;
                }
            }

            Thread.Sleep(200);
        }
    }

    static void StopLink()
    {
        if (link == null) { Warn("No link is active."); return; }
        link.CancelWait();
        detectThread?.Join(500);
        detectThread = null;
        link.Dispose();
        link = null;
        SetWatcherActive(false); // file edits are inert outside link mode
        Log.Info("Link stopped.");
        Balloon("Link stopped - ViPad released.");
        UpdateStatus();
    }

    static void ToggleMode()
    {
        if (link == null) { Warn("No link is active. Start Link first."); return; }
        link.ToggleMode();
        Balloon(link.Mode == LinkMode.Linked
            ? "Mode: LINKED - cooperative, all inputs merged"
            : "Mode: SPLIT - Pad 1 = left side, Pad 2 = right side");
        UpdateStatus();
    }


    // FileSystemWatcher fires Changed/Created/Deleted/Renamed on a THREADPOOL
    // thread, often several times per editor save — coalesce with a debounce
    // window, wait a beat for the write to finish, then reload on the UI thread
    // (DoReload touches menu items). A WinForms Timer created here would never
    // tick — no message pump on pool threads.
    static void SetWatcherActive(bool on)
    {
        try { if (_watcher != null) _watcher.EnableRaisingEvents = on; }
        catch (Exception ex) { Log.Exception("SetWatcherActive", ex); }
    }

    static void ScheduleReload()
    {
        if (link == null) return; // no link running → nothing to reload
        if ((DateTime.Now - _lastReload).TotalMilliseconds < ReloadDebounceMs) return;
        _lastReload = DateTime.Now;
        _reloadTimer?.Dispose();
        _reloadTimer = new System.Threading.Timer(_ =>
        {
            _sync?.Post(__ => { DoReload(); _reloadTimer?.Dispose(); _reloadTimer = null; }, null);
        }, null, 250, Timeout.Infinite);
    }

    // strict all-or-nothing: on any parse error the previous mappings stay
    // active, the error is surfaced, and Restore backup appears in the menu
    static void DoReload()
    {
        // File deleted, or emptied in a raw editor (no [Pad1]/[Pad2] left):
        // the template was destroyed, almost certainly by accident — silently
        // running pass-through with a "reloaded" balloon only confuses. Write
        // the default template instead; the .bak from the last Edit stays
        // reachable via Restore backup.
        bool templateRestored = false;
        if (!System.IO.File.Exists(MappingPath) || FileLooksEmpty())
        {
            // the only destructive write outside reset — keep one undo step
            try
            {
                if (System.IO.File.Exists(MappingPath))
                    System.IO.File.Copy(MappingPath, BackupPath, overwrite: true);
            }
            catch (Exception bex) { Log.Warn($"Could not back up before template restore: {bex.Message}"); }
            MappingConfig.WriteDefaultTemplate();
            _lastReload = DateTime.Now; // our own write must not echo a reload
            templateRestored = true;
            Log.Warn("mapping.ini deleted or emptied - default template restored.");
        }

        try
        {
            var (m1, m2, a1, a2) = MappingConfig.LoadStrict();
            link?.UpdateMappings(m1, m2, a1, a2);
            if (templateRestored)
                Balloon("mapping.ini was deleted or emptied -\ndefault template restored (all pass-through).\nPrevious file: Restore backup / mapping.ini.bak", ToolTipIcon.Warning);
            else
            {
                int count = m1.Remap.Count + m2.Remap.Count +
                            a1.AnalogMap.Count + a2.AnalogMap.Count +
                            a1.ButtonToTrigger.Count + a2.ButtonToTrigger.Count;
                Balloon($"mapping.ini changed - reloaded ({count} active).");
            }
        }
        catch (MappingParseException ex)
        {
            Log.Warn($"mapping.ini rejected: {ex.Message} - keeping previous mappings.");
            BalloonError($"mapping.ini has errors, current mappings kept:\n{ex.Message}\n\nClick to fix the file.");
        }
        // Restore backup visibility is derived from the file's existence in
        // UpdateMenuLabels (runs on every menu open) — no manual show/hide
        // here: a reset's own reload must not hide the undo it just created.
    }

    // true when the file is readable but contains no [Pad1]/[Pad2] header —
    // the signature of an emptied file. Unreadable is NOT empty: LoadStrict
    // surfaces read errors instead of us overwriting a file mid-save.
    static bool FileLooksEmpty()
    {
        try
        {
            foreach (var raw in System.IO.File.ReadAllLines(MappingPath))
            {
                var t = raw.Trim();
                if (t.Equals("[Pad1]", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("[Pad2]", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
        catch { return false; }
    }

    // Ctrl+Shift+click: wipe all mappings back to the empty default template.
    // Backup first, so Restore backup can undo the reset.
    static void ResetMappings()
    {
        var confirm = MessageBox.Show(
            "Reset ALL mappings?\n\nmapping.ini is replaced with the empty default template —\n"
            + "all buttons pass through. The current file is kept as mapping.ini.bak.",
            "ViPadLinker", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;
        try
        {
            if (System.IO.File.Exists(MappingPath))
                System.IO.File.Copy(MappingPath, BackupPath, overwrite: true);
            MappingConfig.WriteDefaultTemplate();
            _lastReload = DateTime.Now; // suppress the echo of our own write
            Log.Info("mapping.ini reset to the default template (backup kept).");
            DoReload();
        }
        catch (Exception ex)
        {
            Log.Exception("ResetMappings", ex);
            Warn("Reset failed: " + ex.Message);
        }
    }

    static void RestoreBackup()
    {
        try
        {
            if (!System.IO.File.Exists(BackupPath))
            {
                Warn("No backup file exists yet.");
                return;
            }

            // Validate the backup BEFORE it can clobber a healthy mapping.ini.
            // A reset taken from a broken config leaves a broken .bak; blindly
            // copying it would replace a working file with the very error the
            // user was escaping. The .bak stays on disk either way, so the
            // work-in-progress is never lost — only restored when it is valid.
            string bakText = System.IO.File.ReadAllText(BackupPath);
            try { MappingConfig.ValidateText(bakText); }
            catch (MappingParseException ex)
            {
                var open = MessageBox.Show(
                    "The backup (mapping.ini.bak) is itself invalid:\n" + ex.Message +
                    "\n\nYour current mapping.ini was left untouched. Open the backup as raw text to fix it?",
                    "ViPadLinker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (open == DialogResult.Yes)
                {
                    try { System.Diagnostics.Process.Start(BackupPath); }
                    catch (Exception oex) { Log.Exception("OpenBackup", oex); }
                }
                return;
            }

            var confirm = MessageBox.Show(
                "Restore mapping.ini from mapping.ini.bak?\n\nThe current mapping.ini will be overwritten.",
                "ViPadLinker", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            System.IO.File.Copy(BackupPath, MappingPath, overwrite: true);
            var (m1, m2, a1, a2) = MappingConfig.LoadStrict();
            link?.UpdateMappings(m1, m2, a1, a2);
            // the undo is consumed — delete the .bak so the entry disappears
            try { System.IO.File.Delete(BackupPath); } catch { }
            Log.Info("mapping.ini restored from backup.");
            Balloon("mapping.ini restored from backup.");
        }
        catch (Exception ex)
        {
            Log.Exception("RestoreBackup", ex);
            Warn("Restore failed: " + ex.Message);
        }
    }

    // dropdown-only GUI editor — the primary way to configure mappings.
    // It writes mapping.ini itself; the FileSystemWatcher reloads it, so no
    // explicit reload here (and the editor keeps open on save errors).
    static void OpenMappingEditor()
    {
        try
        {
            MappingEditor.Open();
        }
        catch (Exception ex)
        {
            Log.Exception("OpenMappingEditor", ex);
            Warn("Could not open the mapping editor: " + ex.Message);
        }
    }

    static void EditMappings()
    {
        try
        {
            MappingConfig.EnsureExists();
            MappingConfig.OpenInShellEditor();
            Log.Info("Opened mapping.ini in default editor.");
            Balloon("Opened mapping.ini.\nWhile a link runs, saves auto-reload - errors keep the current mappings.");
        }
        catch (Exception ex)
        {
            Log.Exception("EditMappings", ex);
            Warn("Could not open mapping.ini: " + ex.Message);
        }
    }

    static void OpenLog()
    {
        var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ViPadLinker.log");
        try
        {
            if (!System.IO.File.Exists(path)) { Warn("Log file not created yet."); return; }
            System.Diagnostics.Process.Start(path);
        }
        catch (Exception ex)
        {
            Log.Exception("OpenLog", ex);
            Warn("Could not open log: " + ex.Message);
        }
    }

    // ── Dynamic menu content ──────────────────────────────────────────────────

    // Called on every menu open — labels reflect current state so the user
    // sees counts and the active mode without opening anything.
    static void UpdateMenuLabels()
    {
        _addHolderItem.Text   = viPads.Count > 0 ? $"Add ViPad ({viPads.Count} held)" : "Add ViPad (hold slot)";
        _removeHolderItem.Text = viPads.Count > 1
            ? $"Remove ViPad ({viPads.Count} held)   Ctrl = all"
            : viPads.Count == 1
                ? "Remove ViPad (1 held)"
                : "Remove ViPad";

        if (link != null)
            _toggleModeItem.Text = link.Mode == LinkMode.Linked
                ? "Toggle mode  (now: LINKED)"
                : "Toggle mode  (now: SPLIT)";
        else
            _toggleModeItem.Text = "Toggle LINKED / SPLIT";

        // single rule for the undo entry: visible exactly while an undo exists
        _restoreItem.Visible = System.IO.File.Exists(BackupPath);
    }

    // Slots holding a physical pad that the app is NOT using (not a holder ViPad,
    // not a link pad). Cheap: 4 XInputGetState calls, run only when the menu
    // opens or after an action — no polling.
    static List<uint> FreePhysicalSlots()
    {
        var viPadSlots = new HashSet<uint>();
        // same lock the pad watcher uses — every mutation is UI-thread too, but
        // reading the shared list locked keeps that invariant enforced, not assumed
        lock (viPads)
            foreach (var pad in viPads)
                try { viPadSlots.Add((uint)pad.UserIndex); } catch { }

        var found = new List<uint>();
        for (uint i = 0; i < 4; i++)
        {
            if (link != null && i == 0) continue;              // link virtual pad
            if (link != null && link.PadIndexForSlot(i) >= 0) continue; // link pad
            if (viPadSlots.Contains(i)) continue;              // our holder ViPad
            if (XInput.GetState(i, out _)) found.Add(i);       // physical pad
        }
        return found;
    }

    // " - 1 physical" / " - 2 physical (slots 2, 4)" — the app may be idle but
    // the slots aren't; the status line must not claim "no pads" while a real
    // pad sits on the second slot. Display is 1-based (SlotLabel), internals 0-based.
    static string PhysicalSuffix(List<uint> slots)
    {
        if (slots.Count == 0) return "";
        var names = string.Join(", ", slots.ConvertAll(s => SlotLabel.N(s)));
        return $" - {slots.Count} physical (slot{(slots.Count > 1 ? "s" : "")} {names})";
    }

    // ── Status ────────────────────────────────────────────────────────────────

    internal static void UpdateStatus()
    {
        if (_icon == null) return;

        string status;
        Icon   stateIcon;
        if (link != null)
        {
            // Twin dots: grey = not connected (yet), green = live, red = disconnected.
            // Same visual language in LINKED and SPLIT — dots describe pad health,
            // the mode stays in the status text/tooltip.
            int left  = PadDotState(0);
            int right = PadDotState(1);
            stateIcon = _iconLinkDots[left, right]!;

            string PadWord(int dot) => dot switch
            {
                PadDotLive => "OK",
                PadDotGone => "GONE",
                _          => "-",
            };
            status = link.IsWaiting && left == PadDotMissing
                ? "Link: waiting for Pad 1..."
                : $"Link {(link.Mode == LinkMode.Linked ? "LINKED" : "SPLIT")}  P1 {PadWord(left)}  P2 {PadWord(right)}";
            // link mode: its pads ARE physical pads — counting strays would
            // double-report; the slot strip shows any extra P markers.
        }
        else
        {
            // Holder / idle: one 4-slot map instead of a count badge — it shows WHICH
            // slots are held, not just how many, and a physical pad that is not ours
            // becomes visible without opening the menu. All slots free = bare app icon
            // (dots only mean something once a device exists).
            int held = viPads.Count;
            var phys = FreePhysicalSlots();
            status = held > 0
                ? $"Holding {held} slot(s)" + PhysicalSuffix(phys)
                : phys.Count == 0
                    ? "Idle - no pads"
                    // idle = the APP does nothing, but a physical pad may still be
                    // connected — say so instead of claiming "no pads"
                    : "Idle (app)" + PhysicalSuffix(phys);
            stateIcon = SlotMapIcon();
        }

        if (!ReferenceEquals(_icon.Icon, stateIcon)) _icon.Icon = stateIcon;
        _statusItem.Text = status;
        // feed the owner-drawn strip; the placeholder text only reserves width
        // (4 cells + gaps + padding), the glyphs themselves are painted
        _stripTokens = SlotTokens();
        _slotStripItem.Text = StripPlaceholder();
        var tip = $"ViPadLinker - {status}";
        _icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; // tooltip hard limit

        _addHolderItem.Enabled    = link == null;
        _removeHolderItem.Enabled = link == null && viPads.Count > 0;
        _startLinkItem.Enabled    = link == null && viPads.Count == 0;
        _stopLinkItem.Enabled     = link != null;
        _toggleModeItem.Enabled   = link != null;
    }

    // ── Notifications ─────────────────────────────────────────────────────────

    // error balloons are clickable: the click handler opens mapping.ini
    static bool _errorBalloonPending;

    static void BalloonError(string msg)
    {
        _errorBalloonPending = true;
        Balloon(msg, ToolTipIcon.Error);
    }

    static void Balloon(string msg, ToolTipIcon kind = ToolTipIcon.Info)
    {
        Log.Info($"[tray] {msg.Replace("\n", " | ")}");
        // a newer balloon replaces the old one on screen, so the pending-error
        // click target is only ever the most recent balloon — reset it here and
        // let BalloonError set it after this call
        if (kind != ToolTipIcon.Error) _errorBalloonPending = false;
        var ic = _icon;
        if (ic == null) return;
        // detection thread is not the UI thread — marshal through the
        // WinForms sync context; direct call is fine when already on UI thread
        if (_sync != null && _sync != SynchronizationContext.Current)
            _sync.Post(_ => { if (_icon != null && _icon.Visible) _icon.ShowBalloonTip(3000, "ViPadLinker", msg, kind); }, null);
        else if (ic.Visible)
            ic.ShowBalloonTip(3000, "ViPadLinker", msg, kind);
    }

    static void Warn(string msg)
    {
        Log.Warn(msg);
        Balloon(msg, ToolTipIcon.Warning);
    }

    // CrashReport hook (Program wires it): a recovered UI-thread exception is
    // announced like any other event — the tray survives and keeps working.
    internal static void NotifyCrash(string msg) =>
        Balloon($"Something went wrong but the app kept running:\n{msg}\n\nDetails: ViPadLinker.log",
                ToolTipIcon.Error);

    // ── Quit ──────────────────────────────────────────────────────────────────

    static void Quit()
    {
        _padWatcherRun = false;
        _padWatcher?.Join(700);
        _padWatcher = null;
        if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); _watcher = null; }
        if (link != null)
        {
            link.CancelWait();
            detectThread?.Join(500);
            link.Dispose();
            link = null;
        }
        foreach (var pad in viPads) Pad.Release(pad);
        viPads.Clear();
        try { client?.Dispose(); }
        catch (Exception ex) { Log.Exception("Quit dispose client", ex); }

        if (_icon != null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
        // releases the handle the second-launch waiter would otherwise poke;
        // the waiter thread itself exits on the disposed handle
        Program.ReleaseShowSignal();
        Log.Info("ViPadLinker exited cleanly.");
        Application.Exit();
    }
}
