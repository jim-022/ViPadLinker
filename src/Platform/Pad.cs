using System;
using Nefarius.ViGEm.Client.Targets;

// ─── ViPad lifecycle ─────────────────────────────────────────────────────

static class Pad
{
    // Disconnect + Dispose, always as a pair. The concrete Xbox360Controller
    // implements IDisposable but the IXbox360Controller interface hides it, so
    // the compiler never enforces it. Dispose releases the NATIVE ViGEm target
    // handle (ViGEmTarget.Dispose/Finalize); Disconnect alone only removes the
    // virtual device and leaves the handle alive until the finalizer gets around
    // to it — memory then climbs with every add/remove or link start/stop cycle.
    public static void Release(IXbox360Controller pad)
    {
        try { pad.Disconnect(); }
        catch (Exception ex) { Log.Exception("pad disconnect", ex); }
        try { (pad as IDisposable)?.Dispose(); }
        catch (Exception ex) { Log.Exception("pad dispose", ex); }
    }
}
