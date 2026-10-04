using System;
using System.Threading;
using System.Windows.Forms;
using Nefarius.ViGEm.Client;

// ─── Entry point ──────────────────────────────────────────────────────────────
// Bootstrap only: CLI args, single-instance guard, ViGEm client, tray creation,
// auto-start actions, message loop. All menu/state logic lives in Ui/TrayApp.

static class Program
{
    // CLI auto-start flags (read by TrayApp: BuildTray balloon + StartLink mode)
    internal static bool _startLink;
    internal static bool _startSplit;   // --link split: toggle to SPLIT right after the session starts
    internal static int  _startPads;

    // Named event the live instance waits on: a second launch pokes it so the
    // running app opens its menu at the cursor instead of the second instance
    // popping a modal.
    static EventWaitHandle? _showSignal;
    const string ShowSignalName = "ViPadLinker.Tray_ShowSignal";

    // Called by TrayApp.Quit: releases the handle a second launch would
    // otherwise poke; the waiter thread exits on the disposed handle.
    internal static void ReleaseShowSignal()
    {
        try { _showSignal?.Dispose(); } catch { }
        _showSignal = null;
    }

    [STAThread]
    static void Main(string[] args)
    {
        // Global exception handlers — first statement, before any window exists
        // (SetUnhandledExceptionMode throws once a handle is created). A UI-thread
        // exception is logged + ballooned and the tray keeps running; a background
        // one is logged and announced with a blocking dialog before the process
        // ends (CLR policy — the point is the user and the log both learn why).
        CrashReport.Install();
        CrashReport.Crash += (msg, fatal) =>
        {
            if (fatal)
                MessageBox.Show(
                    "ViPadLinker hit an unexpected error and will close.\n\n" + msg +
                    "\n\nDetails: ViPadLinker.log",
                    "ViPadLinker", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
                TrayApp.NotifyCrash(msg);
        };

        // DPI awareness is declared in app.manifest (embedded into the exe at
        // build time — no extra release file). It must be a manifest because
        // Application.SetHighDpiMode is .NET Core 3+ only, absent on net48.
        if (!TryParseArgs(args))
        {
            if (args.Length > 0) ShowHelp();
            return;
        }

        // Single-instance guard. The tray is the only front-end now (the console
        // app was removed), so no other ViPadLinker binary contends for ViGEm slots.
        using var mutex = new Mutex(true, "ViPadLinker.Tray_SingleInstance", out bool isNewInstance);
        if (!isNewInstance)
        {
            // Already running: poke the live instance to open its menu, then exit
            // quietly. Replaces the old modal "already running" popup, which was
            // annoying for what is really a "show me" gesture. TryOpenOrCreate so a
            // launch racing the first instance's startup still gets a handle to set.
            try
            {
                using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName, out _);
                ev.Set();
            }
            catch { }
            return;
        }

        // Live instance: create the named event a second launch pokes. Done right
        // after the mutex is ours, so the window where the mutex is held but the
        // event does not yet exist is negligible.
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);

        Log.Init();
        Log.Info("ViPadLinker started.");
        MappingConfig.EnsureExists();

        try
        {
            TrayApp.client = new ViGEmClient();
            Log.Info("Connected to ViGEmBus.");
        }
        catch (Exception ex)
        {
            Log.Exception("ViGEmClient init", ex);
            MessageBox.Show(
                "Could not connect to ViGEmBus driver.\n\n" + ex.Message +
                "\n\nMake sure ViGEmBus is installed:\nhttps://github.com/nefarius/ViGEmBus/releases",
                "ViPadLinker", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        TrayApp.BuildTray();

        // Background waiter: a second launch pokes the named event → open the menu
        // at the cursor. Runs off the UI thread; ShowTrayMenu marshals back via
        // _sync itself. IsBackground so it never keeps the process alive; the
        // WaitOne throws only if the handle is disposed at shutdown, ending the loop.
        new Thread(() =>
        {
            while (true)
            {
                try { _showSignal.WaitOne(); }
                catch { return; }

                TrayApp.ShowTrayMenu();
            }
        }) { IsBackground = true }.Start();

        // auto-start actions run once the message loop is up — StartLink/AddViPad
        // touch NotifyIcon, so they must execute on the UI thread
        if (_startLink || _startPads > 0)
        {
            var oneShot = new System.Windows.Forms.Timer { Interval = 200 };
            oneShot.Tick += (_, __) =>
            {
                oneShot.Stop();
                oneShot.Dispose();
                if (_startLink) TrayApp.StartLink();
                for (int i = 0; i < _startPads; i++) TrayApp.AddViPad();
                TrayApp.UpdateStatus();
            };
            oneShot.Start();
        }

        Application.Run();
    }

    // ── CLI arg parsing ────────────────────────────────────────────────────
    // A WinExe has no console, so help and arg errors are shown in a
    // MessageBox instead of stdout.

    static bool TryParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--help":
                case "-h":
                    return false; // help shown by caller
                case "--link":
                case "-l":
                    _startLink = true;
                    // optional mode value: --link linked (default) | --link split.
                    // Only consumed when the next token is a known mode, so
                    // "--link --pads 2" still fails the exclusivity check below.
                    if (i + 1 < args.Length)
                    {
                        var mode = args[i + 1].ToLowerInvariant();
                        if (mode == "linked") { }
                        else if (mode == "split") _startSplit = true;
                        else if (mode.StartsWith("-")) { } // no value given
                        else
                        {
                            ArgError($"--link mode must be 'linked' or 'split', got '{args[i + 1]}'.");
                            return false;
                        }
                        if (mode == "linked" || mode == "split") i++;
                    }
                    break;
                case "--pads":
                case "-p":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n > 0 && n <= 4)
                    {
                        _startPads = n;
                        i++;
                    }
                    else
                    {
                        ArgError("--pads requires a positive integer (1-4).");
                        return false;
                    }
                    break;
                default:
                    ArgError($"Unknown argument '{args[i]}'.");
                    return false;
            }
        }

        if (_startLink && _startPads > 0)
        {
            ArgError("--link and --pads cannot be combined " +
                     "(Link owns slot 1, holder pads would fill the rest).");
            return false;
        }
        if (_startSplit && !_startLink)
        {
            ArgError("'split' is a --link mode, not a standalone option.");
            return false;
        }
        return true;
    }

    static void ArgError(string msg)
    {
        Log.Warn($"CLI arg error: {msg}");
        MessageBox.Show(msg + "\n\nRun with --help for usage.",
            "ViPadLinker", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    static void ShowHelp()
    {
        MessageBox.Show(
            "ViPadLinker — gamepad slot & link manager in the system tray\n\n" +
            "Usage:\n" +
            "  ViPadLinker.exe                  Start in tray (idle)\n" +
            "  ViPadLinker.exe --link  (-l)     Start in Link mode immediately\n" +
            "                      linked|split  mode after start (default: linked)\n" +
            "  ViPadLinker.exe --pads N (-p N)  Add N virtual pads (1-4)\n" +
            "  ViPadLinker.exe --help  (-h)     Show this help\n\n" +
            "Examples:\n" +
            "  ViPadLinker.exe -l               Start link, LINKED mode\n" +
            "  ViPadLinker.exe -l split         Start link, SPLIT mode\n\n" +
            "Notes:\n" +
            "  --link and --pads are mutually exclusive.\n" +
            "  All actions remain available from the tray menu.\n" +
            "  Only one instance can run at a time - ViGEm slots are exclusive.",
            "ViPadLinker", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
