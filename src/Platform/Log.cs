using System;
using System.IO;

// ─── Logger ───────────────────────────────────────────────────────────────────

static class Log
{
    static readonly string LogPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "ViPadLinker.log");
    static readonly object Lock     = new object();
    static bool            _enabled = true;

    public static void Init()
    {
        try
        {
            File.WriteAllText(LogPath,
                $"ViPadLinker started {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        }
        catch
        {
            // no console in a WinExe to complain to — logging just turns itself off
            _enabled = false;
        }
    }

    public static void Write(string level, string msg)
    {
        if (!_enabled) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}";
        lock (Lock)
        {
            try { File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch
            {
                _enabled = false;   // disk full/unlocked — go silent, never throw from a log call
            }
        }
    }

    public static void Info(string msg)  => Write("INFO ", msg);
    public static void Warn(string msg)  => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    public static void Exception(string context, Exception ex) =>
        Write("ERROR", $"{context}: {ex.Message}\n{ex.StackTrace}");
}
