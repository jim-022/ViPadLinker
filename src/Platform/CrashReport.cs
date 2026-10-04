using System;
using System.Threading.Tasks;
using System.Windows.Forms;

// ─── Crash reporting ──────────────────────────────────────────────────────────
// Global exception handlers. A tray app has no window to crash, so the default
// WinForms dialog ("Continue / Quit" with a stack trace) is the worst possible
// UX — and without any handler at all, a UI-thread exception kills the app
// silently from the user's point of view.
//
// Policy:
//  • UI-thread exception  → log + balloon, app KEEPS RUNNING (the tray must
//    survive; one broken menu action must not take down held slots or a link).
//  • Other-thread exception → log + balloon, the process is going down anyway
//    (CLR policy) — the point is that the log says why.
//  • Faulted Task          → log (observed, so it does not escalate).
//
// This class never touches the UI itself — it raises Crash so the app decides
// how to announce (Platform/ must not depend on Ui/).

static class CrashReport
{
    // message already formatted for a balloon; fatal = the process will exit
    public static event Action<string, bool> Crash;

    public static void Install()
    {
        // Replace the WinForms UI-thread dialog with our handler. Must be set
        // before any window exists. A handler is always attached below, so
        // AED_None (not the throw-rethrow default) is the right mode.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception, false);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(e.ExceptionObject as Exception, e.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report(e.Exception, false);
            e.SetObserved();   // observed — do not escalate to a crash
        };
    }

    static void Report(Exception ex, bool fatal)
    {
        if (ex == null) return;
        Log.Error($"UNHANDLED ({(fatal ? "fatal" : "recovered")}): {ex}");
        Crash?.Invoke($"{ex.GetType().Name}: {ex.Message}", fatal);
    }
}
