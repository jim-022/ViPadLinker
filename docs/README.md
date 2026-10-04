# ViPadLinker — Manual

← [project overview](../README.md)

A lightweight portable Windows system-tray tool for XInput controller slot management and cooperative pad linking.  
Two features: **Slot Holder** — occupy XInput slots to push physical pads to a desired slot, and **Pad Link** — combine one or two physical pads into a single virtual controller for single-player co-op, with per-pad button remapping.

---

## Requirements

- Windows 10 or 11
- [ViGEmBus driver](https://github.com/nefarius/ViGEmBus/releases) — install once per machine, admin required
- .NET Framework 4.8 — already present on all Windows 10/11 machines

---

## Files Setup (Portable)

```
📁 InstallDir
├── ViPadLinker.exe            ← the app
├── Nefarius.ViGEm.Client.dll  ← must sit next to the exe
└── mapping.ini                ← button remapping config (auto-created on launch if missing)
```

First time on a new PC: install the **ViGEmBus driver** — it is *not* shipped
with ViPadLinker and must be downloaded separately from the
[official ViGEmBus releases page](https://github.com/nefarius/ViGEmBus/releases)
(run `ViGEmBus_setup.exe`, admin required, once per machine). Without the
driver, ViPadLinker shows a "Could not connect to ViGEmBus driver" error on
startup with the download address.

After driver installation just run `ViPadLinker.exe` — the icon appears in the system tray.

`mapping.ini` is created automatically on first launch if not present.  
A log file `ViPadLinker.log` is created next to the exe on each launch (overwritten each run).

---

## The Tray App

Click the icon (left or right) for the menu:

```
Status line (live: idle / holding N slots / link mode + pad states)
Slot strip       [ L  |  1  |  !  |  · ]   (see legend below)
─────────────────────────────────
Add ViPad (hold slot)
Remove ViPad
─────────────────────────────────
Start Link
Stop Link
Toggle mode (shows current mode)
─────────────────────────────────
Edit mappings…  (visual editor)
Restore backup  (appears while an undo exists)
Open log
─────────────────────────────────
Quit
```

**Slot strip legend:** each slot is drawn as a small colored cell with its letter —
blue = your virtual pad (`L` link LINKED, `V` holder), deeper blue = link in SPLIT (`S`),
green = live link pad (`1` `2`), orange-red = link pad lost (`!`), grey = physical pad
not linked (`P`), faint outline = empty (`·`).
Hover the strip for the legend.

- The icon, tooltip, status line and slot strip refresh when you open the menu and
  immediately on pad connect/disconnect events.
- **Ctrl+click "Remove ViPad"** releases every held ViPad in one go (the menu label
  reminds you when 2+ are held).
- **Edit mappings…** opens the mapping editor (see below); **Ctrl+click** opens the raw
  `mapping.ini` in your default text editor; **Ctrl+Shift+click** resets all mappings to
  the empty template (the previous file is kept as `mapping.ini.bak` — undo via
  Restore backup).
- **Physical-pad watcher:** while idle or holding slots, the tray quietly watches all
  four XInput slots. A physical pad (one the app is not using) that connects or
  disconnects gets a balloon + chime — handy for verifying multi-pad layouts as you
  build them. A change must persist ~1 second before it is announced, so brief
  wireless dropouts stay silent. Paused while a link is active (link mode reports
  its own pads), and pads already plugged in at startup never balloon.
- The status line tells the whole truth: when the app itself is idle or holding
  slots but a **physical pad** is still connected, it says so
  (`Idle (app) - 1 physical (slot 2)`) instead of claiming "no pads". The icon stays
  bare — idle means the app does nothing, which is still true.

> **Slot numbering:** the app shows the four XInput slots as **1-4** (virtual and
> physical share the same pool — virtual pads take the lowest free slots first).
> The log file and the raw XInput API use **0-3**, so app "slot N" = log slot "N-1".

- **Tray icon states:** in holder/idle mode, small dots form a 2×2 map of the four
  slots (bottom-left = 1, bottom-right = 2, top-left = 3, top-right = 4) — blue =
  your ViPad, green = physical pad. Empty slots draw nothing; all slots free = the
  bare app icon. In link mode, two dots at the bottom corners show pad health:
  grey = not connected (yet), green = live, red = lost.
- Events (pad detected, link active, pad lost, errors) arrive as balloon notifications.
- **Sounds:** a soft rising two-note chime plays when a pad connects, a falling
  chime when one disconnects. The chimes are embedded in the tray exe — no sound
  files to ship.
- **Mapping lifecycle:** `mapping.ini` is read when you **Start Link** and watched for
  changes only while a link runs — outside link mode, edits sit inert until the next
  Start Link. Loading is strict: one bad line rejects the whole file, the running
  mappings stay active, and an error balloon (with line number) appears — **click it to
  open the file** and fix it. A broken file never blocks linking: the link starts with
  all inputs passing through and tells you so.
- **Undo:** the app writes `mapping.ini.bak` only right before it replaces your file
  itself (reset, or restoring the template after the file was deleted/emptied). While
  that backup exists, **Restore backup** offers one-click recovery — the backup is
  validated before it can overwrite a working file.
- **Single instance:** launching a second copy simply opens the running app's menu at
  the cursor — no duplicate processes, no popup dialogs.

### The Mapping Editor

Opened from **Edit mappings…**. One side shows a gamepad outline, the other the
settings for the selected pad — **Pad 1 / Pad 2 tabs**, each with **BUTTONS** (the 14
digital buttons) and **TRIGGERS / STICKS** (LT, RT, LStick, RStick) columns:

- **Every value comes from a dropdown** that lists only the legal targets for that row,
  so what the editor saves can never be a broken file. "default" = unmapped (passes
  through).
- **Click a control on the gamepad** to jump to its dropdown; hovering shows what it is
  mapped to. The outline mirrors your settings live:
  **blue = remapped**, **red = blocked**, **orange = conflict** (two buttons aim at the
  same target — legal, but usually a mistake).
- **Swap pairing:** setting LT↔RT (or the sticks) to *swap* disables the counterpart row
  — a swap is one setting for both sides.
- **Save** writes the file and keeps the editor open for more tweaks; the running link
  reloads automatically. A tab shows **`*`** while it has unsaved changes, and closing
  with unsaved changes asks **Save / Discard / Cancel**.
- **Restore defaults** clears the current pad; **Ctrl+click** clears both pads.
- ⚠ on a trigger/stick row means: **SPLIT mode is active and this swap acts as block**
  (each pad only owns one side, so there is nothing to swap with).

### Command-Line Arguments

Start in a specific mode without touching the menu:

```
ViPadLinker.exe                  Start in tray (idle)
ViPadLinker.exe --link  (-l)     Start link mode immediately
                    linked|split  mode after start (default: linked)
ViPadLinker.exe --pads N (-p N)  Add N virtual pads (1-4)
ViPadLinker.exe --help  (-h)     Show usage dialog
```

`--link` and `--pads` are mutually exclusive. Unknown arguments are rejected with an
error dialog. When auto-starting, only the specific action balloon is shown (no generic
"Running" balloon on top of it).

---

## Use Cases

### Push a physical pad to slot 2 (split-screen, P1 = keyboard + mouse)

XInput assigns slots in connection order. To make a physical pad land on slot 2 (Player 2) instead of slot 1 (Player 1):

1. Run `ViPadLinker.exe`
2. Menu → **Add ViPad** — virtual pad occupies slot 1
3. Plug in your physical gamepad — it lands on slot 2
4. Launch your game — game sees slot 1 as P1 (keyboard/mouse), slot 2 as P2 (physical pad)
5. Menu → **Remove ViPad** when done

> **Note:** Add multiple ViPads to push a pad further — each holds one additional slot. Two pads held = physical pad lands on slot 3, and so on.

---

### Link one or two pads to single virtual controller (single-player co-op)

Two players share control of a single-player game. The game sees one controller on slot 1:

1. Run `ViPadLinker.exe`
2. Menu → **Start Link**
3. App creates virtual linked pad on slot 1 — **do not plug anything in yet**
4. When prompted by balloon: plug in pad 1 → link becomes active immediately
5. Plug in pad 2 at any time — it joins the link automatically (no prompt needed)
6. Launch your game — it sees one controller on slot 1
7. Menu → **Toggle mode** to switch between LINKED and SPLIT at any time
8. Menu → **Stop Link** to release, **Quit** to exit

**LINKED mode (default):**
- Buttons: either pad pressing = pressed (each pad's remap applied independently before merging)
- Triggers: harder press from either pad wins
- Sticks: whichever is pushed further from centre wins
- Rumble: forwarded to both pads

**SPLIT mode:**
- Pad 1 owns: left stick, left trigger, D-pad, LB, LS, Back
- Pad 2 owns: right stick, right trigger, face buttons (A/B/X/Y), RB, RS, Start
- Each pad only controls its assigned side — cross-side inputs are ignored
- Each pad's remap applies only to its own side
- **Analog swaps (LT↔RT, LStick↔RStick) act as block in SPLIT** — each pad carries only
  one side, so a swap has no counterpart to trade with. The mapping editor marks such
  rows with a ⚠ while a SPLIT link is active.

> **Single-player games** do not need HidHide — they only read slot 1 and ignore physical pads on slots 2 and 3.

---

## Button Remapping

`mapping.ini` is created automatically next to the exe on first launch. The primary way to configure remapping is the menu's **Edit mappings…** — a visual editor with a clickable gamepad outline and dropdown-only choices, so what it saves cannot be broken (see *The Mapping Editor* above). Prefer hand-editing? **Ctrl+click "Edit mappings…"** opens the raw file.

The file is read when a link **starts** and watched for changes while a link runs; outside link mode, edits apply at the next Start Link.

Any button not listed passes through unchanged. Valid button names:

```
A  B  X  Y
LB  RB
LS  RS  (left/right stick click)
Start  Back
Up  Down  Left  Right
```

**Example — Nintendo layout swap on pad 1:**
```ini
[Pad1]
A = B
B = A
X = Y
Y = X

[Pad2]
```

**Example — swap LB and RB on pad 2:**
```ini
[Pad1]

[Pad2]
LB = RB
RB = LB
```

In LINKED mode each pad's remapping is applied to its own inputs independently before merging — so pad 1 and pad 2 can have completely different layouts without interfering with each other.

---

## Building from Source

Requires [.NET SDK](https://dotnet.microsoft.com/download) (any recent version).

```
.\BUILD.bat
```

Copy from `bin\release\` to your USB stick:
- `ViPadLinker.exe`
- `Nefarius.ViGEm.Client.dll`
- `mapping.ini` (optional auto-created on launch)

---

## Troubleshooting

**"Could not connect to ViGEmBus driver"**  
Install ViGEmBus: https://github.com/nefarius/ViGEmBus/releases

**Virtual pad doesn't appear on slot 1**  
Another app or a previously connected controller may have claimed slot 1. Unplug all physical pads and restart ViPadLinker.

**"A pad is already on slot 1"**  
Unplug the physical pad on slot 1 before starting a link. ViPadLinker needs slot 1 free for the virtual pad.

**Pad 2 not detected automatically**  
The detection thread scans slots 1–3 every ~200ms. Make sure the pad is fully connected and Windows has recognised it. The menu's slot strip shows what the app sees in every slot.

**Pad disconnected during link**  
The link keeps running. For the first 2 seconds the disconnected pad's inputs freeze at their last state (a held button stays held) — this bridges brief wireless dropouts. If the pad is still gone after 2 seconds, its inputs are zeroed so nothing stays stuck (a running character stops, a held trigger releases). A balloon and chime mark the disconnect. Reconnect the pad at any time — it resumes live input automatically with a confirmation chime.

**Button remapping not working**  
Mappings load when a link starts — edit before **Start Link**, or save while the link runs (the auto-reload balloon shows the active-mapping count). A rejected file keeps the previous mappings and shows a clickable error balloon; `ViPadLinker.log` has the exact line and reason.

**Nothing happens when launching again**  
ViPadLinker is single-instance — the app already runs in the system tray. Launching a second copy opens the running app's menu at the cursor.
