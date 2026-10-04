using System;
using System.Collections.Generic;
using System.Threading;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;

// ─── Link mode ────────────────────────────────────────────────────────────────

enum LinkMode { Linked, Split }

// ─── Link session ─────────────────────────────────────────────────────────────

class LinkSession : IDisposable
{
    class PadSlot
    {
        public volatile uint Slot;
        public volatile bool Alive;
        public int  FailTicks;   // consecutive GetState failures — poll thread only
        public bool Zeroed;      // freeze timeout already announced — poll thread only
    }

    // ~2s of continuous GetState failure = permanent disconnect (dead battery,
    // dongle unplugged). Short enough to rescue a stuck stick/trigger, long
    // enough to bridge wireless dropouts (phantom drops run up to ~50 ms).
    const int FreezeTimeoutTicks = 500;

    readonly IXbox360Controller _vpad;
    readonly List<PadSlot>      _pads   = new List<PadSlot>();
    volatile bool               _polling;
    volatile bool               _running = true;
    readonly Thread             _pollThread;
    XINPUT_STATE                _last1;  // last good state — poll thread only
    XINPUT_STATE                _last2;

    // ── rumble forwarding (poll thread) ───────────────────────────────────────
    volatile byte  _rumbleLarge, _rumbleSmall;   // latest values from the game
    byte           _sentLarge,   _sentSmall;     // last values actually sent to pads
    int            _rumbleCooldown;              // ticks until next rumble update allowed

    // poll error dedupe — a persistent exception would otherwise log a full
    // stack trace ~250x/sec. First occurrence logs immediately, then every
    // 250th (≈1/sec); any clean tick resets the counter.
    int            _pollErrorCount;
    bool           _pollErrorShown;

    volatile ButtonMapping      _map1;
    volatile ButtonMapping      _map2;
    volatile AnalogMapping      _amap1;
    volatile AnalogMapping      _amap2;

    // cross-thread flags — written by main thread, read by poll/detect threads
    volatile bool _isWaiting = true;
    volatile LinkMode _mode = LinkMode.Linked;

    // Raised from the POLL thread when a linked pad connects or disconnects.
    // (padIndex 0-based, physical slot, alive). The tray subscribes to surface
    // balloons. Zeroing is intentionally NOT reported here (silent architectural
    // safety net, not a user event).
    public event Action<int, uint, bool>? PadStatusChanged;

    public int                       PadCount      => _pads.Count;
    public bool                      IsRunning     => _running;
    public LinkMode                  Mode          => _mode;
    public bool                      IsWaiting     => _isWaiting;

    public bool PadAlive(int index)
    {
        lock (_pads) { return index < _pads.Count && _pads[index].Alive; }
    }

    public int PadIndexForSlot(uint slot)
    {
        lock (_pads)
        {
            for (int i = 0; i < _pads.Count; i++)
                if (_pads[i].Slot == slot) return i;
            return -1;
        }
    }

    public LinkSession(ViGEmClient client, ButtonMapping map1, ButtonMapping map2,
                       AnalogMapping amap1, AnalogMapping amap2)
    {
        _map1  = map1;
        _map2  = map2;
        _amap1 = amap1;
        _amap2 = amap2;

        _vpad = client.CreateXbox360Controller();

        // CRITICAL: without this, every SetButtonState/axis assignment submits its
        // own report to the driver. A clear-then-set pass then broadcasts
        // a release→press pulse for every held button each tick, and a game polling
        // between the two sees the button momentarily released (breaks charge/hold
        // actions). With auto-submit off, only the single SubmitReport() at the end
        // of each tick reaches the driver — the whole tick becomes atomic.
        _vpad.AutoSubmitReport = false;

        // rumble from game → recorded here, forwarded by the POLL thread.
        // Two reasons: (1) ViGEm fires FeedbackReceived far more often than the
        // game intends while rumble is active — flooding XInputSetState at a
        // wireless pad makes it drop INPUT packets (held buttons glitch loose,
        // which breaks charge attacks — most games rumble while charging).
        // (2) forwarding from this callback thread ran XInputSetState
        // concurrently with the poll thread's XInputGetState on the same device.
        _vpad.FeedbackReceived += (_, e) =>
        {
            _rumbleLarge = e.LargeMotor;
            _rumbleSmall = e.SmallMotor;
        };
        _vpad.Connect(); // grabs slot 0

        _pollThread = new Thread(PollLoop) { IsBackground = true };
        _pollThread.Start();
    }

    // called by WaitForPads when a new pad is detected
    public void AddPad(uint slot)
    {
        lock (_pads) { _pads.Add(new PadSlot { Slot = slot, Alive = true }); }

        if (_pads.Count == 1)
        {
            _polling   = true;
            _isWaiting = false;
            Log.Info($"Link session: pad 1 on slot {slot}, polling started.");
        }
        else
        {
            Log.Info($"Link session: pad {_pads.Count} on slot {slot}, joined.");
        }
    }

    // signals detection thread to exit cleanly
    public void CancelWait()
    {
        _isWaiting = false;
    }

    public void ToggleMode()
    {
        _mode = _mode == LinkMode.Linked ? LinkMode.Split : LinkMode.Linked;
        Log.Info($"Link mode changed to {_mode}");
    }

    // swap mappings live — takes effect on next poll cycle
    public void UpdateMappings(ButtonMapping map1, ButtonMapping map2,
                                AnalogMapping amap1, AnalogMapping amap2)
    {
        _map1  = map1;
        _map2  = map2;
        _amap1 = amap1;
        _amap2 = amap2;
        Log.Info("Mappings updated live.");
    }

    void PollLoop()
    {
        while (_running)
        {
            try
            {
                if (_polling)
                {
                    // snapshot pad state once per tick — guards against concurrent AddPad()
                    int  padCount;
                    uint slot1;
                    uint slot2;
                    bool hasPad2;
                    lock (_pads)
                    {
                        padCount = _pads.Count;
                        slot1    = padCount > 0 ? _pads[0].Slot : 0;
                        slot2    = padCount > 1 ? _pads[1].Slot : 0;
                        hasPad2  = padCount > 1;
                    }

                    bool p1 = XInput.GetState(slot1, out var s1);
                    if (p1)
                    {
                        _last1 = s1;
                        _pads[0].Zeroed = false;
                        _pads[0].FailTicks = 0;
                    }
                    else
                    {
                        s1 = _last1; // transient read failure or disconnect —
                                     // freeze at last good state instead of applying
                                     // a zeroed struct (which released every held
                                     // button for a tick)
                        if (++_pads[0].FailTicks == FreezeTimeoutTicks)
                        {
                            // permanent disconnect — release everything from this pad
                            _last1 = default;
                            s1     = default;
                            if (!_pads[0].Zeroed)
                            {
                                _pads[0].Zeroed = true;
                                // NO sound here — the disconnect chime already played on
                                // the Alive transition; zeroing is the silent safety net
                                // (log only), otherwise every real disconnect chimes twice.
                                Log.Warn($"Pad 1 (slot {slot1}) failed {FreezeTimeoutTicks} ticks - zeroing frozen state.");
                            }
                        }
                    }
                    if (p1 != _pads[0].Alive)
                    {
                        _pads[0].Alive = p1;
                        PadStatusChanged?.Invoke(0, slot1, p1);
                        if (p1) { Sound.Connected();    Log.Info($"Pad 1 (slot {slot1}) GetState OK again."); }
                        else    { Sound.Disconnected(); Log.Warn($"Pad 1 (slot {slot1}) GetState FAILED - freezing at last state (buttons=0x{_last1.Gamepad.wButtons:X4})."); }
                    }

                    XINPUT_STATE s2 = default;
                    if (hasPad2)
                    {
                        bool p2 = XInput.GetState(slot2, out s2);
                        if (p2)
                        {
                            _last2 = s2;
                            _pads[1].Zeroed = false;
                            _pads[1].FailTicks = 0;
                        }
                        else
                        {
                            s2 = _last2; // freeze, same as pad 1
                            if (++_pads[1].FailTicks == FreezeTimeoutTicks)
                            {
                                _last2 = default;
                                s2     = default;
                                if (!_pads[1].Zeroed)
                                {
                                    _pads[1].Zeroed = true;
                                    // NO sound here — see pad 1 note (single chime per event).
                                    Log.Warn($"Pad 2 (slot {slot2}) failed {FreezeTimeoutTicks} ticks - zeroing frozen state.");
                                }
                            }
                        }
                        if (p2 != _pads[1].Alive)
                        {
                            _pads[1].Alive = p2;
                            PadStatusChanged?.Invoke(1, slot2, p2);
                            if (p2) { Sound.Connected();    Log.Info($"Pad 2 (slot {slot2}) GetState OK again."); }
                            else    { Sound.Disconnected(); Log.Warn($"Pad 2 (slot {slot2}) GetState FAILED - freezing at last state (buttons=0x{_last2.Gamepad.wButtons:X4})."); }
                        }
                    }

                    var g1 = s1.Gamepad;
                    var g2 = hasPad2 ? s2.Gamepad : default;

                    if (Mode == LinkMode.Linked)
                    {
                        // buttons — per-pad remap independently, merge into one mask,
                        // then set each button's FINAL state exactly once (no
                        // clear-then-set pulse for held buttons)
                        ushort merged = (ushort)(_map1.ComputeTargets(g1.wButtons) |
                                                 _map2.ComputeTargets(g2.wButtons));
                        ButtonMapping.ApplyMask(_vpad, merged);

                        // Linked mode analog — apply each pad's mapping to its own raw values first
                        byte  lt1 = g1.bLeftTrigger,  rt1 = g1.bRightTrigger;
                        short lx1 = g1.sThumbLX,      ly1 = g1.sThumbLY;
                        short rx1 = g1.sThumbRX,      ry1 = g1.sThumbRY;

                        byte  lt2 = g2.bLeftTrigger,  rt2 = g2.bRightTrigger;
                        short lx2 = g2.sThumbLX,      ly2 = g2.sThumbLY;
                        short rx2 = g2.sThumbRX,      ry2 = g2.sThumbRY;

                        _amap1.Apply(_vpad, ref lt1, ref rt1, ref lx1, ref ly1, ref rx1, ref ry1, g1.wButtons);
                        _amap2.Apply(_vpad, ref lt2, ref rt2, ref lx2, ref ly2, ref rx2, ref ry2, g2.wButtons);

                        // then merge
                        byte  lt = Math.Max(lt1, lt2);
                        byte  rt = Math.Max(rt1, rt2);
                        short lx = Dominant(lx1, lx2);
                        short ly = Dominant(ly1, ly2);
                        short rx = Dominant(rx1, rx2);
                        short ry = Dominant(ry1, ry2);

                        _vpad.LeftTrigger  = lt;
                        _vpad.RightTrigger = rt;
                        _vpad.LeftThumbX   = lx;
                        _vpad.LeftThumbY   = ly;
                        _vpad.RightThumbX  = rx;
                        _vpad.RightThumbY  = ry;
                    }
                    else // Split
                    {
                        // buttons — each pad owns its side
                        _map1.ApplyLeft(_vpad,  g1.wButtons);
                        _map2.ApplyRight(_vpad, g2.wButtons);

                        // analog — each pad owns its side, apply mappings independently
                        byte  lt = g1.bLeftTrigger;
                        byte  rt = g2.bRightTrigger;
                        short lx = g1.sThumbLX, ly = g1.sThumbLY;
                        short rx = g2.sThumbRX, ry = g2.sThumbRY;

                        // pad 1 owns left side — pass zeroes for right side inputs
                        byte  p1Rt = 0; short p1Rx = 0, p1Ry = 0;
                        _amap1.Apply(_vpad, ref lt, ref p1Rt, ref lx, ref ly, ref p1Rx, ref p1Ry, g1.wButtons);

                        // pad 2 owns right side — pass zeroes for left side inputs
                        byte  p2Lt = 0; short p2Lx = 0, p2Ly = 0;
                        _amap2.Apply(_vpad, ref p2Lt, ref rt, ref p2Lx, ref p2Ly, ref rx, ref ry, g2.wButtons);

                        _vpad.LeftTrigger  = lt;
                        _vpad.RightTrigger = rt;
                        _vpad.LeftThumbX   = lx;
                        _vpad.LeftThumbY   = ly;
                        _vpad.RightThumbX  = rx;
                        _vpad.RightThumbY  = ry;
                    }

                    _vpad.SubmitReport();

                    // forward rumble — deduped (only on change) and rate-limited
                    // (≥ ~32 ms between updates) so pads are never flooded with
                    // output reports; a stop (0,0) always passes immediately so
                    // rumble can't stick on.
                    // NOTE: the global _sent* dedupe assumes every ALIVE pad receives
                    // every send — deliberate. A pad that joins or reconnects while a
                    // steady rumble value is held stays silent until the value next
                    // changes (any effect stop sends 0 = a change). Per-pad last-sent
                    // would close that gap but changes nothing in steady state, so it
                    // was considered and rejected as not worth the hot-path churn.
                    if (_rumbleCooldown > 0) _rumbleCooldown--;
                    byte rl = _rumbleLarge, rs = _rumbleSmall;
                    bool rumbleChanged = rl != _sentLarge || rs != _sentSmall;
                    bool rumbleStop    = rl == 0 && rs == 0;
                    if (rumbleChanged && (_rumbleCooldown == 0 || rumbleStop))
                    {
                        List<PadSlot> snapshot;
                        lock (_pads) { snapshot = new List<PadSlot>(_pads); }
                        foreach (var pad in snapshot)
                            if (pad.Alive) XInput.SetVibration(pad.Slot, rl, rs);
                        _sentLarge = rl; _sentSmall = rs;
                        _rumbleCooldown = 8; // 8 ticks ≈ 32 ms
                    }
                }

                _pollErrorCount = 0;   // clean tick — reset error dedupe
                _pollErrorShown = false;
            }
            catch (Exception ex)
            {
                // dedupe: first error + every ~1s thereafter, never 250x/sec
                _pollErrorCount++;
                if (!_pollErrorShown)
                {
                    _pollErrorShown = true;
                    _pollErrorCount = 0;
                    Log.Exception("PollLoop", ex);
                }
                else if (_pollErrorCount >= 250)
                {
                    _pollErrorCount = 0;
                    Log.Error($"PollLoop still failing ({ex.Message})");
                }
            }

            Thread.Sleep(4); // ~250Hz
        }
    }

    static short Dominant(short a, short b) =>
        Math.Abs((int)a) >= Math.Abs((int)b) ? a : b;

    public void Dispose()
    {
        _running = false;
        _polling = false;
        _isWaiting = false;    // also stops the detection thread from adding more pads
        _pollThread.Join(500);
        PadSlot[] snapshot;
        lock (_pads) { snapshot = _pads.ToArray(); }
        foreach (var pad in snapshot)
            try { XInput.StopVibration(pad.Slot); } catch { }
        // Disconnect alone leaves the native ViGEm target handle alive until the
        // finalizer runs — Dispose releases it now (see Pad.Release).
        Pad.Release(_vpad);
        Log.Info("LinkSession disposed.");
    }
}