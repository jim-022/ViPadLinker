# Why ViPadLinker exists

← [project overview](../README.md)

## 🎮 Rocket League, and a controller that wouldn't behave

It started at a friend's place. He's a keyboard-and-mouse die-hard, so I always
brought a pad — and getting a second controller to show up reliably was its own
mini-project. We rotated whatever pad-emulation tool was popular that month
(x360ce most of all). Each one wanted to drop a DLL into the game folder, bent
the polling rate, or slipped a translation layer between the pad and the game.
We just wanted to play, and I wanted my game to stay *native*. Bringing a
second pad was its own hassle too — the kind that powers itself off right when
you need it.

The idea came from remote-play software: those tools quietly create a virtual
Xbox pad on slot 1 through [ViGEmBus](https://github.com/nefarius/ViGEmBus),
and the game never notices. So I wrote a throwaway PowerShell script that did
the same thing — park a virtual pad on a slot so a real controller lands where
I want it. That became the **Slot Holder**.

## 🧒 A kid, a right stick, and a lot of "here, let me show you"

Then a second reason showed up. My kid loves playing games with me but is still
learning to navigate 3D spaces with the right stick and pull off two-thumb
combos — and grabbing the controller out of little hands to "help" never helped
anybody.

Windows had actually grown a native way to combine two controllers for exactly
this, for assisted play or just fun. But it wanted official Xbox pads for both
players, and its mapping for ordinary controllers wasn't good enough. So the
script grew a second half: merge two pads into one, remap each side, hand the
game a single controller. That became **Pad Link**.

## 🛠️ A name, then a real app

With both halves working, it finally had a name worth building a real app
around: **ViPadLinker** — *Vi*rtual *Pad* *Linker*. It started as a console app,
tackling one bug after another, and slowly became something I was genuinely
happy with. Then the small nice features arrived — like starting from the
command line with arguments, so you can make a shortcut for a game and spin up
the link before the game even launches. No more "Daddy, can I turn on my
gamepad already?".

## 💛 Sharing it

Eventually I wanted to make it more accessible and friendly — something I could
share with anybody, not just whoever would run a script I wrote. That's what
this repo is: lightweight, no install, quick to spin up. If it saves someone
else a few tool-rotating nights, it's done its job.
