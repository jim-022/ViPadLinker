using System;

// ─── Mapping config loader ────────────────────────────────────────────────────

// thrown by MappingConfig.LoadStrict when the file cannot be trusted —
// callers keep their previous mappings and surface the message
class MappingParseException : Exception
{
    public MappingParseException(string message) : base(message) { }
}
