# ViPadLinker — Developer Notes & Context

This document covers the architecture, design decisions, known limitations, and suggested next steps for anyone continuing development on ViPadLinker.

---

## What It Does

ViPadLinker is a .NET Framework 4.8 system-tray app (WinForms `NotifyIcon`) with two independent features:

**Slot Holder** — Creates one or more dummy virtual XInput controllers via ViGEmBus. Each virtual pad occupies one XInput slot in connection order. Physical pads plugged in afterwards land on higher slot numbers. Useful for forcing a physical pad to be Player 2 while Player 1 uses keyboard and mouse natively.

**Pad Link** — Creates one virtual XInput controller on slot 0, waits for pad 1 on a background thread (the UI stays responsive, Stop Link cancels), then starts polling immediately. Pad 2 is optional and joins automatically in the background at any time. A poll thread runs at ~250Hz, merges input from both physical pads according to the current link mode, applies per-pad button remapping independently, and submits the merged state to the virtual pad. Rumble is forwarded to both physical pads. Pad disconnect and reconnect are detected and surfaced with sound and balloon notifications.

---

## Architecture

### Dependencies

| Library | Version | Role |
|---|---|---|
| `Nefarius.ViGEm.Client` | 1.21.256 | Creates and controls virtual XInput controllers |
| `xinput1_4.dll` | Windows inbox | Reads physical pad state and sends rumble — raw P/Invoke, no NuGet |
| ViGEmBus driver | any supported | Kernel driver backing virtual controllers — separate install |

### Key classes

**`Log` (static)** — file logger, the only user-invisible output channel (the console app was removed, so there is no stdout anywhere). Overwrites `ViPadLinker.log` on each launch. Thread-safe append. Disables itself gracefully if the file can't be written.

**`Sound` (static)** — plays embedded WAV chimes on dedicated background threads so the poll loop never blocks. `Connected()` = rising two-note chime, `Disconnected()` = falling two-note chime. Resources are looked up by exact logical name (`connect.wav`, `disconnect.wav`); the tray csproj embeds them (`EmbeddedResource`). `SoundPlayer.PlaySync` on a per-sound background thread (never `Play()` — it needs a message pump to clean up its stream). Fires on pad connect/disconnect (link mode, tray watcher). Regenerate the WAVs with `tools/Make-Sounds.ps1` (synthesized sines + envelope, tweakable tone list).

**`XInput` (static)** — thin P/Invoke wrapper around `xinput1_4.dll`. `GetState`, `SetVibration`, `StopVibration`. Motor scale: ViGEm `byte` (0–255) → XInput `ushort` (0–65535).

**`ButtonNames` (static)** — bidirectional map between human-readable names (`"A"`, `"LB"`, `"Start"` etc.) and `Xbox360Button` enum values. Used by `MappingConfig` for parsing and display.

**`ButtonMapping`** — holds a per-pad remap dictionary (`Xbox360Button` → `Xbox360Button`). Apply methods:
- `ComputeTargets(raw)` — computes the remapped virtual button mask **without touching the pad**. Linked mode computes both pads' masks, ORs them, then `ApplyMask` sets each button's final state exactly once.
- `ApplyMask(pad, mask)` (static) — sets every button to pressed/released from a merged mask in a single pass. No clear-then-set churn.
- `ApplyLeft` — clears and sets left-side buttons only (Split mode pad 1).
- `ApplyRight` — clears and sets right-side buttons only (Split mode pad 2).

Remapping can move a button to a different virtual target — every path sets each button's final state exactly once per tick, so old and new targets can never both stay pressed.

**`AnalogMapping`** — per-pad mapping of analog sources (`LT`, `RT`, `LStick`, `RStick`) with three target kinds: `Block` (= none), `Swap` (trigger↔trigger or stick↔stick only, mixing rejected by the parser), and `Button` (trigger fires a digital button on any non-zero press). Also handles digital button → max trigger (`A = LT`). `Apply()` modifies trigger/stick values in place; called after button apply so button→trigger combos work.

**`MappingConfig` (static)** — loads and parses `mapping.ini` from the exe directory. INI format with `[Pad1]` and `[Pad2]` sections. The parser is **strict-only** (the lenient line-skipping loader left with the console app): the first bad line throws `MappingParseException` with line number + content. `EnsureExists()` creates the file with a template on first launch; `WriteDefaultTemplate()` (re)writes that same template (reset, self-heal). API: `LoadStrict()` (link start, watcher reload, editor open), `ValidateText(string)` (same parser against a string, no disk — the GUI editor checks its own output before writing), `Empty()` (pass-through config — link starts with it when the file is broken), `OpenInShellEditor()`. The parser core works on text (`ParseText`), file reading is a thin wrapper. GOTCHA (fixed): `Enum.TryParse<AnalogSource>("1")` succeeds as `RT` — `TryParseAnalogSource` rejects leading digit/sign so numeric values can't sneak in as swaps; name lookups go through dictionaries only, never framework parsers.

**`MappingEditor`** (`src/Ui/MappingEditor.cs`) — dropdown-only GUI over `mapping.ini`, opened from the tray menu. Prevention design: every row's ComboBox contains exactly the legal targets for that source (buttons: 14 names + none + LT/RT; triggers: none/swap/button; sticks: none/swap only), so the file it writes cannot contain parser errors. Model is two `PadDoc`s (remap / button→trigger / analog dicts mirroring the file); "default" = absent = passthrough. Swap pairing: setting one side to swap clears + disables the counterpart row (a swap is one mapping for both sides). Conflict highlighting: two sources aiming at one destination tint both rows (and the canvas turns orange). Refuses to open a broken file (would show defaults and one Save would destroy work) — offers raw-text editing instead. Save validates in memory (`ValidateText`) before touching disk.

Layout: `SplitContainer` — gamepad canvas left (column auto-fitted to the art's aspect ratio), tab pages right; each tab is two columns: BUTTONS (14 digital rows) and TRIGGERS (LT/RT) + STICKS (LStick/RStick). GOTCHA: canvas `Dock=Left` + tabs `Dock=Fill` lays the tabs UNDER the canvas (dock z-order) — dropdowns popped outside the form; the SplitContainer gives each side a real rectangle. The canvas (`GamepadCanvas.cs`) is a selector, not an editor: zones (circle/ring/rect, hit-test in reverse add order) raise `RegionClicked(id)` → the matching dropdown focuses and opens; focusing a dropdown highlights the zone back. Outer stick ring = analog row, inner knob = stick click. State colors: blue = remapped, red = blocked, orange = conflict, accent ring = selected; hover tooltip carries the detail (`→ B`, `swap RT`). Geometry is authored in virtual units, packed into a tight content box (`_content` = body + zones + stroke) and scaled to the control — no dead margin; labels are centered by rasterising glyphs to a path and centering its tight bounds (font-metric guesses sat round letters off-center).

Editing flow: **Save writes and stays open** (no `DialogResult` side effect). Dirty state is **content-based**: a baseline clone of each `PadDoc` is captured at open and after every successful Save; a pad is dirty only while it differs from its baseline — manually reverting an edit clears the flag, resetting an already-default pad never marks dirty (compare runs after swap-pairing cleanup, which also mutates the doc). Tab caption shows `Pad N *` while that pad is dirty. Every close path (X, Esc, Close button) routes through `FormClosing` — a `DialogResult`-carrying button would end the modal loop WITHOUT firing it (that silently skipped the guard). While dirty it prompts Save/Discard/Cancel; a failed Save keeps the form open. Restore defaults: click = current pad, Ctrl+click = both pads.

**`LinkSession`** — owns one `IXbox360Controller` (the virtual linked pad), a list of `PadSlot` entries (slot + alive flag), a `LinkMode`, two `ButtonMapping` + two `AnalogMapping` instances, and a background poll thread. Key API:
- `IsWaiting` — true from construction until pad 1 joins (`AddPad`) or `CancelWait()` is called
- `AddPad(slot)` — adds a pad; first pad starts polling and clears `IsWaiting`; pad 2 joins any time
- `CancelWait()` — sets `IsWaiting = false` to signal the background detection thread to exit
- `UpdateMappings(map1, map2, amap1, amap2)` — swaps mappings live (volatile fields), takes effect next poll cycle
- `ToggleMode()` — toggles Linked/Split, takes effect next poll cycle
- `PadAlive(index)` / `PadIndexForSlot(slot)` — pad state queries used by the tray icon/status

**`TrayApp`** — `static partial class` across `TrayApp.cs` (menu, watcher, lifecycle), `TrayApp.Icons.cs` (icon generation), `TrayApp.Strip.cs` (owner-drawn slot row); bootstrap lives in `src/Program.cs`. Holds the list of slot-holder pads, optional single `LinkSession`, and the tray UI state. See the Tray App section below for the full design.

---

## CLI Arguments

| Flag | Purpose |
|---|---|
| `--link` / `-l` | Start link mode immediately (creates LinkSession, waits for Pad 1). Optional value `linked` (default) or `split` — `--link split` toggles once right after creation; any other value is a parse error |
| `--pads N` / `-p N` | Add N virtual pads (1-4) |
| `--help` / `-h` | Show usage dialog and exit (accepted anywhere in the argument list) |

Implementation: `TryParseArgs()` scans all args, sets `_startLink` / `_startPads`. Unknown
arguments and invalid `--pads` values show an error dialog and exit. `--link` and
`--pads` are mutually exclusive — combining them is an error (Link owns slot 0, holder
pads would fill the rest). Auto-start actions run once the message loop is up, via a
one-shot 200 ms WinForms timer (NotifyIcon work requires the UI thread).

---

## XInput Slot Assignment

Windows assigns XInput slots (0–3) in connection order. ViGEmBus virtual controllers appear as real USB devices. By creating a virtual pad first, it takes slot 0. Physical pads land on slots 1, 2, 3.

```
Start Link → virtual pad created → grabs slot 0
             background thread starts watching slots 1-3
plug pad 1 → detected on slot 1   → AddPad(1) → polling starts
plug pad 2 → detected on slot 2   → AddPad(2) → joins automatically, any time
game reads slot 0 → sees merged virtual pad
```

---

## ViGEm API Notes (v1.21.256)

- `Xbox360Controller` is **`internal`** — always use `client.CreateXbox360Controller()` which returns `IXbox360Controller`
- No `Xbox360Report`, no `SendReport`, no `Xbox360Buttons` (plural)
- State via ref properties + `SubmitReport()`:
  ```csharp
  pad.LeftTrigger  = value;   // byte
  pad.RightTrigger = value;   // byte
  pad.LeftThumbX   = value;   // short
  pad.SetButtonState(Xbox360Button.A, true);
  pad.SubmitReport();         // must call after setting state
  ```
- Rumble: `e.LargeMotor` and `e.SmallMotor` are `byte` (0–255)

---

## Link Mode Detail

### LINKED mode

Each pad's remapping is applied to its own raw inputs **independently** before merging:

```
pad 1 raw buttons → _map1.ComputeTargets() → mask 1 ─┐
                                                     ├─ OR → ApplyMask() → single SubmitReport()
pad 2 raw buttons → _map2.ComputeTargets() → mask 2 ─┘
```

This means pad 1 and pad 2 can have completely different button layouts without interference,
and each virtual button gets its final state exactly once per tick (atomic — see
`AutoSubmitReport = false` note in `LinkSession`).

| Input | Logic |
|---|---|
| Buttons | Per-pad remap → OR merge via `ComputeTargets` + `ApplyMask` |
| Triggers | `Math.Max` — harder press wins |
| Sticks | Dominant — larger `Math.Abs((int)value)` wins |
| Rumble | Forward to both pads |

### SPLIT mode

Each pad's mapping applies strictly to its own side:

| Input | Owner |
|---|---|
| Left stick, left trigger | Pad 1 |
| Right stick, right trigger | Pad 2 |
| D-pad, Back, LB, LS | Pad 1 |
| A/B/X/Y, Start, RB, RS | Pad 2 |

---

## Link Cancellation Flow

```
Start Link
  → virtual pad created on slot 0
  → background detection thread started (WaitForPads with session ref)
  → UI returns immediately (message pump keeps running)

Stop Link (while still waiting for pad 1)
  → link.CancelWait() → sets IsWaiting = false
  → WaitForPads checks IsWaiting at top of each 200ms loop → returns
  → link.Dispose() → virtual pad released
  → link = null
```

`StopLink` calls `CancelWait()`, joins the detection thread, then disposes the session —
works in both the waiting and the active state.

---

## Known Issues & Gotchas

### Hold-qualified debounce (REMOVED)
A hold-qualified release debounce was implemented once and later deleted (was commented
out in `LinkSession.cs` until it was removed — see git history if it is ever wanted again).
The root cause of hold-breaking was the `AutoSubmitReport = true` + clear-then-set pattern,
not phantom drops. With `AutoSubmitReport = false` and frozen state on disconnect, the
atomic report eliminates the issue entirely; the debounce added latency with no benefit.
Design findings worth keeping, in case drop-bridging is ever revisited:
- A *fixed* release-debounce window cannot work: phantom radio drops (up to ~50 ms) and
  genuine mash-tap releases (20–50 ms) overlap in duration, so any fixed window either
  breaks holds or glues taps together.
- The separating signal is press age *before* the release: a charge/hold is continuous for
  hundreds of ms, a mash press lasts ~60–100 ms. Only long presses should earn bridging;
  taps must forward releases instantly (zero added latency).

### Freeze timeout on disconnect
On `GetState` failure the poll loop freezes the pad at its last good state (rather than
zeroing, which would release every held button for a tick and break holds through brief
wireless dropouts). But a *permanent* disconnect (dead battery, dongle unplugged) would
otherwise freeze a stuck stick/trigger forever. So each `PadSlot` counts consecutive
failures; after `FreezeTimeoutTicks` (500 ticks ≈ 2s) the frozen state is zeroed once and
a warning is logged. `Zeroed` guards against repeat announcements; a successful read
resets `FailTicks` and `Zeroed`, resuming live input. 2s is long enough to bridge phantom
drops (≤ ~50ms) yet short enough to rescue a runaway input.
**Zeroing is silent on purpose:** the disconnect chime plays once, on the `Alive`
transition (first failed read) — the same instant the tray dot turns red and the balloon
fires. An earlier version also chimed at the zeroing moment, so every real disconnect
sounded twice 2s apart; the second chime was removed (log-only).

### OverflowException in Dominant (fixed)
`Math.Abs(short.MinValue)` overflows. Fix in place — cast to `int` before `Abs`:
```csharp
Math.Abs((int)a) >= Math.Abs((int)b) ? a : b
```

### Console output (removed)
The console app and the shared `Con` helper were removed (console deprecation). All
diagnostics go to `Log`; all user-facing messages are balloons or dialogs. Do not
reintroduce `Console.*` writes — a WinExe has no console attached and the writes are
silent no-ops at best.

### HidHide not implemented
For split-screen games that enumerate all XInput slots, the game sees physical pads on slots 1 and 2 alongside the virtual linked pad on slot 0. For single-player games this is fine. For multi-slot games, HidHide (`Nefarius.Drivers.HidHide` NuGet) would hide physical pads from the game process.

### WaitForPads — pad 2 detection race
The `WaitForPads` detection thread scans slots 1–3 every 200ms. If a pad is already connected on slots 1–3 before Start Link is pressed, it will be picked up as pad 2 immediately after pad 1 connects. This is expected behaviour in practice.

### UpdateMappings thread safety
`_map1`/`_map2`/`_amap1`/`_amap2` on `LinkSession` are `volatile` references swapped by
`UpdateMappings`; the poll loop reads them each cycle without a lock. A torn read across
the four fields is still theoretically possible (one cycle could mix old map1 with new
map2) — harmless, one frame of mixed state. `IsWaiting` and `Mode` are also `volatile`.

---

## Suggested Next Steps

**Cancellation UX**
`WaitForPads` polls every 200ms — Stop Link during the waiting state takes up to 200ms
to take effect. Could reduce to 50ms for snappier feel.

**HidHide integration**
Add `Nefarius.Drivers.HidHide` NuGet. During active link, hide physical pads from all processes except ViPadLinker. Required for split-screen games.

**Preset files**
Allow multiple named mapping files (e.g. `presets/nintendo.ini`). Add a submenu to cycle presets live during an active link.

**Hardware alternative**
A Raspberry Pi Pico or ESP32-S2/S3 enumerating as VID `045E` PID `028E` (Xbox 360) with TinyUSB occupies a slot with zero software on the host PC. Windows `xusb22.sys` binds natively.

---

## File Structure

```
ViPadLinker/
├── src/
│   ├── ViPadLinker.csproj    — WinExe, WinForms; one project over src/ (default globbing — new .cs files need no csproj entry)
│   ├── Program.cs            — entry point: CLI args, single-instance guard, ViGEm client, auto-start, message loop
│   ├── Mapping/              — mapping.ini domain (no UI, no OS plumbing)
│   │   ├── ButtonNames.cs        — canonical name ↔ Xbox360Button tables
│   │   ├── ButtonMapping.cs      — digital button remap/block, Linked + Split apply
│   │   ├── AnalogMapping.cs      — trigger/stick block/swap/button mapping
│   │   ├── MappingConfig.cs      — strict parser + template writer (the config file)
│   │   └── MappingParseException.cs
│   ├── Linking/              — link domain: merge logic + mode semantics
│   │   └── LinkSession.cs        — virtual pad on slot 0, 250Hz poll thread, merge modes, rumble forwarding
│   ├── Platform/             — OS/hardware plumbing, no domain knowledge
│   │   ├── XInputNative.cs       — XInput structs + P/Invoke (xinput1_4.dll)
│   │   ├── Log.cs                — file logger (self-disabling on write failure)
│   │   ├── Sound.cs              — embedded WAV chimes, played on a bg thread
│   │   ├── Pad.cs                — ViPad lifecycle (Disconnect + Dispose pair)
│   │   └── SlotLabel.cs          — 0-based internal ↔ 1-based display
│   └── Ui/                   — everything with pixels
│       ├── TrayApp.cs        — partial TrayApp: NotifyIcon menu, watcher, pad/link lifecycle
│       ├── MappingEditor.cs  — dropdown-only mapping.ini editor (prevention GUI over the parser)
│       ├── GamepadCanvas.cs  — GDI+ gamepad outline: clickable zones, state colors, tight-content scaling
│       ├── TrayApp.Icons.cs  — partial TrayApp: status icon generation (4-slot map + twin pad dots)
│       └── TrayApp.Strip.cs  — partial TrayApp: owner-drawn slot strip row (cells, colors, cached GDI)
├── assets/
│   ├── Icon.ico              — app/tray icon (ApplicationIcon in the csproj)
│   └── sounds/               — connect.wav / disconnect.wav (embedded into the tray exe)
├── tools/
│   ├── Get-XInputGamepads.ps1 — list connected XInput gamepads
│   ├── Simulate-Pads.ps1      — test rig: interactive A/R/Q virtual pad holder (tray sees them as physical)
│   ├── Make-Sounds.ps1        — regenerates the connect/disconnect WAV chimes into assets\sounds\
│   └── sample_mapping.ini    — example mapping file
├── ViPadLinker.sln           — solution: the tray project
├── BUILD.bat                 — builds the release binary into bin\release\
├── README.md               — GitHub project overview (storefront; the manual lives in docs/README.md)
├── LICENSE                 — MIT + ViGEmBus acknowledgement
├── mapping.ini               — button remapping config, auto-created on first launch
├── docs/README.md            — user manual
├── docs/WHY.md               — the back-story (why the app exists)
└── docs/DEVNOTES.md          — this file
```

Folder rule: `Ui → Mapping, Linking, Platform`; `Linking → Mapping, Platform`;
`Platform → nothing`. A type's folder answers "what kind of thing is it" —
domain logic (`Mapping`, `Linking`) is kept apart from OS plumbing (`Platform`)
and pixels (`Ui`). No namespaces are used (single assembly, global namespace);
the folders carry the organization.

The console app (`src/console/`, `Program.cs`) was removed — see the console
deprecation checkpoint tag `checkpoint-pre-console-removal` if it is ever needed again.
(The entry point later came back as a fresh `src/Program.cs` holding only bootstrap,
split out of `TrayApp`.) The csproj later moved from `src/tray/` to `src/` so default
globbing compiles the whole source root — the old `EnableDefaultCompileItems=false` +
manual `<Compile Include>` list (and its "forgot to add the file" gotcha) is gone.

---

## Tray App (ViPadLinker)

The only binary — `src/ViPadLinker.csproj` compiles the whole `src/` root with
default globbing: `Program.cs` (bootstrap), the domain folders (`Mapping/`,
`Linking/`), the OS plumbing (`Platform/`) and the UI in `src/Ui/` (no shared DLL,
no `ProjectReference`). New `.cs` files anywhere under `src/` compile automatically —
no csproj entry needed. The tray splits its `static partial class TrayApp` across
`TrayApp.cs` / `TrayApp.Icons.cs` / `TrayApp.Strip.cs`: pure drawing (icons, strip)
separate from app logic; shared static state (`link`, `viPads`, `_icon`) stays in
`TrayApp.cs` and is visible to all parts. `Program` creates the `ViGEmClient` and
calls into `TrayApp` (`BuildTray`, `StartLink`, `AddViPad`, `UpdateStatus`,
`ShowTrayMenu` — all `internal`); the CLI auto-start flags live on `Program`.

Design:
- `WinExe` + WinForms (`UseWindowsForms`, in-box on net48 — no NuGet), `[STAThread]`,
  `Application.Run()` message pump, `NotifyIcon` + `ContextMenuStrip`
- **Single-instance mutex** `ViPadLinker.Tray_SingleInstance`. A second launch does not
  show a modal popup — it pokes a named `EventWaitHandle`
  (`ViPadLinker.Tray_ShowSignal`) and exits quietly; the live instance
  waits on it in a background thread and answers by opening its menu at the cursor
  (`ShowTrayMenu`). The event is created right after the mutex is acquired;
  a launch racing startup uses `TryOpenOrCreate` semantics so the poke still lands.
- **Menu from code — `ShowTrayMenu()`**: both left-click on the icon and the
  second-launch signal open the menu programmatically. `menu.Show()` is *not*
  usable: the dropdown becomes an unowned top-level window (taskbar button while
  open, no click-away dismiss). Instead it invokes `NotifyIcon`'s private
  `ShowContextMenu` via reflection — the same path Windows uses for a real
  right-click (owned popup, click-away dismiss, opens at the cursor). Two
  catches: the call must run on the UI thread (a menu created on a loopless
  thread is invisible and wedges the process — `ShowTrayMenu` hops via `_sync`),
  and a background app's `SetForegroundWindow` is blocked by Windows, so an
  unfocused menu never sees the click-away. Fix: `AttachThreadInput` to the
  foreground thread for the duration of the call (borrows its foreground rights),
  always detached in `finally` — a leaked attach can freeze whichever app hangs.
  Accepted quirk: re-clicking the icon while the menu is open closes and
  immediately re-opens it.
- Menu actions call the core APIs directly (`LinkSession`, `MappingConfig`, `XInput`).
- `ContextMenuStrip.ShowImageMargin = false` — no item uses icons, and Windows paints
  the reserved icon gutter as a gray band; without it the menu carries dead space.
  (The slot-strip cells paint inside the item's text rectangle, so they shift with it.)
- Detection thread raises balloon tips through the WinForms
  `SynchronizationContext` captured on the UI thread (`_sync.Post`) — `NotifyIcon` is not
  thread-safe to touch from the detection thread.
- `mapping.ini` and `ViPadLinker.log` live next to the exe; the log is overwritten at
  each launch and writes are append-safe (background threads log concurrently).
- **CLI auto-start** (`--link`/`-l` with optional `linked|split` value, `--pads N`/`-p N`,
  `--help`/`-h`); `--link split` = create session then one-shot
  `ToggleMode()` (session always starts LINKED, flag consumed once). Generic "Running"
  balloon is suppressed when an auto-start action ran — the action's own balloon says it
  all. Flags and validation live in `TryParseArgs` (`TrayApp.cs`). Because a `WinExe` has no
  console, help and arg errors use `MessageBox` instead of stdout/stderr. The auto-start
  action runs from a one-shot 200ms `WinForms.Timer` after `Application.Run()` starts —
  `StartLink`/`AddViPad` touch `NotifyIcon`, so they must execute on the UI thread.
- Tray icon: `assets/Icon.ico` (set as `ApplicationIcon` in the csproj), extracted
  at startup with `SystemIcons.Application` fallback. All state icons are generated
  (`Icon.FromHandle(bmp.GetHicon())`). Two dot styles, both 13px with a 2px dark ring —
  the artwork (two gamepads + cable tip) fills all four corners, so the ring is what
  keeps dots readable over it.
  - **Holder / idle states: 4-slot map**, one dot per XInput slot in a 2x2 grid,
    **numpad order** — slot 1 bottom-left, 2 bottom-right, 3 top-left, 4 top-right.
    blue = our ViPad, green = physical pad in the slot (ours or not). **Free slots draw
    no dot at all** — a dot only appears where a device is; slot position still says
    *which* slot. When every slot is empty the icon shows no dots at all — bare artwork.
    Dots only carry information once a device exists, and the bare icon is the cheapest
    possible "nothing anywhere" signal (`SlotMapIcon` returns the cached base instance for
    the all-free key, which also keeps the reference-compare swap below working). Answers
    *which* slots are held (the old blue count badge only said how many) and makes a
    newly-plugged physical pad visible without opening the menu.
    The bottom row matches the link twin dots, so the two modes agree along the bottom edge.
    **Lazy icon cache:** 3^4 = 81 combinations, but only a handful ever occur, so each
    is drawn the first time its layout appears and kept for the process lifetime
    (`_slotMapIcons`, keyed by a base-3 slot code) — pre-baking all 81 would paint 81
    bitmaps + HICONs for layouts that never happen. Safe without locking: `UpdateStatus`
    only runs on the UI thread (direct calls, or background events via `_sync.Post`).
  - **Link states** (LINKED *and* SPLIT — dots describe pad health, mode stays in the
    status text): **twin 13px dots at the bottom corners**, left = Pad 1, right = Pad 2,
    shared 3-color set: grey = not connected (yet), green = live, orange-red =
    disconnected. Replaced the old green-2/amber-1/orange-red-N digit badges, which
    could not show *which* pad was lost. Red appears immediately on the first failed
    read — the 2s freeze/zeroing inside `LinkSession` is an input-safety detail and is
    deliberately not visualised (no `Zeroed` accessor or extra event needed; `PadAlive`
    + `PadCount` are sufficient). All 3×3 = 9 combinations are pre-baked (the tray takes
    exactly one bitmap per state — no live layers); unreachable combos cost nothing and
    keep the lookup branch-free.
    COLOR LANGUAGE: **green = a real controller is in that slot**, blue = our virtual
    pad, no dot = vacant (link mode draws grey for "not joined yet" — an active waiting
    state, not an empty slot). Green is consistent across both icon styles, which is why
    the slot map uses green (not slate) for a physical pad. **Orange-red exists in link
    mode only** — a pad we were actively merging input from went away. The slot map has
    no red state on purpose: a ViPad cannot die, and a physical pad leaving a slot is
    normal, not a fault. NOTE: the menu slot strip still paints physical pads slate
    (`P`) — deliberate, it has room for a letter label and does not compete with the icon.
  `UpdateStatus` swaps `_icon.Icon` by reference comparison — no flicker from redundant
  sets. GOTCHA: `StringFormatFlags.NoPadding` does not exist on net48 (Core-only).

Gotchas:
- `NotifyIcon.Text` hard-limits at 63 chars — tooltip is truncated.
- Core files report through `Log` only — there is no console to write to. User-facing
  messages reach the tray via events (`PadStatusChanged`) or return values, surfaced as
  balloons/dialogs. Do not add `Console.*` writes to shared code.
- **`LinkSession.PadStatusChanged` event** (`Action<int padIndex, uint slot, bool alive>`)
  is raised from the poll thread on pad connect/disconnect — the tray subscribes and shows
  a balloon (`Balloon` marshals via `_sync`). Input zeroing after the freeze timeout is deliberately NOT reported —
  it's a silent architectural safety net, not a user-facing event.
- **Dynamic menu content**: `UpdateMenuLabels()` on menu open (pad counts in Add/Remove
  labels, current mode in the toggle label). The slot view is a compact **header strip**
  (`[ L | 1 | ! | · ]`, L/S=link virtual pad in LINKED/SPLIT mode, V=holder ViPad,
  1/2=live link pad, !=lost, P=physical, ·=empty) — replaced an earlier Slots submenu.
  NO 1s timer: `UpdateStatus()` runs on menu open, after every action, and on pad
  events (poll/detect threads marshal via `_sync.Post`). The menu closes on every click
  and balloons confirm actions, so a periodic refresh was dead weight.
  The strip row is **owner-drawn** (colored cells, same visual language as the tray
  dots): `SlotTokens()` returns the 4 tokens, `UpdateStatus` stores them in
  `_stripTokens` and sets the item `Text` to a blank placeholder sized to the painted
  cells (`StripPlaceholder` measures space width). Painting happens in
  `StripCellRenderer : ToolStripProfessionalRenderer` overriding `OnRenderItemText` —
  for the strip item the base call is skipped (placeholder never drawn) and cells are
  painted into `e.TextRectangle` (already past the check gutter — this is what aligns
  the cells with the text column). GOTCHA (net48): `ToolStripItem.OwnerDraw`/`DrawItem`,
  `ToolStripItem.TextRectangle`, the `ContextMenuStrip.RenderItemText` event and
  `StringFormatFlags.NoPadding` are all **Core-only** — the renderer override is the
  only supported owner-draw hook on net48. Earlier attempts via the item `Paint` event
  painted from the item edge (included the gutter, looked shifted left).
- **Physical-pad watcher** (`PadWatcherLoop`, tray only): background thread polling the
  4 slots every 500 ms, announcing physical pads (excluding our holder ViPads and link
  pads) with balloon + chime on connect/disconnect. Design points:
  - Changes must persist **2 consecutive polls (~1 s)** before announcing — phantom
    wireless drops last up to ~50 ms (same phenomenon the link freeze logic defends
    against), so a single failed poll must never balloon.
  - `seeded` flag: pads already plugged in at startup never balloon.
  - While a link is active the watcher **silently re-syncs** its known set and skips
    announcements — link mode owns pad reporting (`PadStatusChanged`) and must not
    double-report. On the link→idle transition it re-seeds silently (`wasLinked`),
    otherwise stopping a link would burst "connected" balloons for pads that stayed
    plugged in.
  - Holder list read under `lock (viPads)` — `AddViPad`/`RemoveViPad` mutate it on the
    UI thread while the watcher enumerates.
  - Status changes marshal via `_sync.Post(UpdateStatus)`. `Quit()` stops the watcher
    first (`_padWatcherRun = false`, `Join(700)`).
- **Embedded sounds**: `assets/sounds/{connect,disconnect}.wav` are `EmbeddedResource`
  items in the tray csproj with `LogicalName` set to the bare filename — the `Sound`
  class looks them up by exact name, so a namespace-prefixed default name would
  silently disable audio. `System.Media.SoundPlayer` plays uncompressed PCM WAV only.
- **Mapping lifecycle** (tray only) — `mapping.ini` is read at **Start Link** and watched
  only while a link runs; outside link mode file edits are inert (watcher created
  disabled, `SetWatcherActive` on Start/StopLink, `ScheduleReload` also guards on
  `link == null`). Rationale: mappings only matter in link mode, so nothing is validated
  at app start and a broken file never blocks anything silently.
  - `MappingConfig.LoadStrict()` — strict all-or-nothing parse; the first bad line throws
    `MappingParseException` with line number + content. Broken at link start → link still
    starts with `Empty()` (pass-through) + ONE combined error balloon (the generic start
    balloon is skipped — Windows shows one toast at a time with a ~5s minimum, two
    back-to-back balloons means the actionable one gets lost).
  - `FileSystemWatcher` on mapping.ini (Changed/Created/Deleted/Renamed) → `ScheduleReload()`
    debounces 400ms, then a **rooted `System.Threading.Timer`** (250ms write-settle) marshals
    via `_sync.Post` to the UI thread. GOTCHA: watcher events fire on a threadpool thread —
    a WinForms Timer created there NEVER ticks (no message pump); first attempt failed this way.
  - Success → apply live + balloon with active-mapping count. Rejection → running mappings
    kept, error balloon. **Error balloons are clickable** (`_errorBalloonPending` flag +
    `BalloonTipClicked`): re-validates first, opens the raw file only if still broken — a
    stale balloon clicked after the fix must not yank open Notepad.
  - Deleted/emptied file while linked → template self-heal (`FileLooksEmpty` = readable
    but no `[Pad1]`/`[Pad2]`; unreadable is NOT empty — LoadStrict surfaces mid-save locks).
  - **`.bak` is scoped undo, not history:** written ONLY right before the app itself
    destroys the file (Ctrl+Shift reset, template self-heal). No backup-on-open, no
    per-save hook. **Restore backup** visibility is derived, not choreographed:
    `UpdateMenuLabels` sets `Visible = File.Exists(BackupPath)` on every menu open (an
    earlier show/hide dance let a reset's own success-reload hide the undo it created).
    Restore validates the `.bak` with `ValidateText` BEFORE copying (a reset taken from a
    broken config leaves a broken `.bak` — copying first would clobber a healthy file),
    and consumes the `.bak` on success.
  - Invariant: **the app never writes `mapping.ini` from data it has not validated** —
    editor (ValidateText before write), restore (validate before copy), reset/self-heal
    (writes the known-good template).

---

## Build

```
.\BUILD.bat
```

`BUILD.bat` runs `dotnet build src\ViPadLinker.csproj -c Release -o bin\release`.
Output in `bin\release\`:
- `ViPadLinker.exe` — the system tray application
- `Nefarius.ViGEm.Client.dll` — ViGEm managed client (~150KB)

Both files must be present alongside `mapping.ini`. .NET Framework 4.8 is pre-installed on every Windows 10/11 machine — no runtime install needed on target machines.

> GOTCHA: `dotnet build ViPadLinker.sln` writes to `src\bin\Release\net48\`, NOT to
> `bin\release\`. Always validate with `BUILD.bat` before testing — the sln build leaves
> `bin\release\` stale.
