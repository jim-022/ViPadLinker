// ─── Slot numbering ───────────────────────────────────────────────────────────
// XInput slots are 0-based (0-3) in the API, in the log file, and in every
// internal variable. User-facing text is 1-based (1-4): "slot 1" is the first
// controller, while "slot 0" reads to users like an error or placeholder.
// Use this for labels only — never when calling XInput.

static class SlotLabel
{
    public static string N(uint zeroBased) => (zeroBased + 1).ToString();
    public static string N(int zeroBased)  => (zeroBased + 1).ToString();
}
