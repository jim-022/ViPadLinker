using System;
using System.Collections.Generic;
using System.IO;
using Nefarius.ViGEm.Client.Targets.Xbox360;

// ─── Mapping config loader ────────────────────────────────────────────────────

static class MappingConfig
{
    static readonly string ConfigPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "mapping.ini");

    public static string ConfigFilePath => ConfigPath;

    // pass-through config — link starts with this when mapping.ini is broken
    public static (ButtonMapping, ButtonMapping, AnalogMapping, AnalogMapping) Empty() =>
        MakeResult(new(), new(), new(), new(), new(), new());

    // open mapping.ini with the shell's associated editor (raw text path)
    public static void OpenInShellEditor() =>
        System.Diagnostics.Process.Start(ConfigPath);

    // strict load: the FIRST bad line rejects the whole file — a half-parsed
    // mapping set is worse than keeping the last good one. Throws
    // MappingParseException; caller keeps current mappings. (The lenient
    // line-skipping loader left with the console app — the tray is the only
    // consumer and it always wants all-or-nothing.)
    public static (ButtonMapping pad1, ButtonMapping pad2, AnalogMapping amap1, AnalogMapping amap2) LoadStrict()
    {
        if (!File.Exists(ConfigPath)) throw new MappingParseException("mapping.ini not found.");
        return ParseText(File.ReadAllText(ConfigPath));
    }

    // validate generated text with the same strict parser, without touching
    // disk — the GUI editor checks its own output before overwriting the file
    public static void ValidateText(string iniText) => ParseText(iniText);

    static (ButtonMapping, ButtonMapping, AnalogMapping, AnalogMapping) ParseText(string text)
    {
        var remap1     = new Dictionary<Xbox360Button, Xbox360Button>();
        var remap2     = new Dictionary<Xbox360Button, Xbox360Button>();
        var analogMap1 = new Dictionary<AnalogSource, AnalogEntry>();
        var analogMap2 = new Dictionary<AnalogSource, AnalogEntry>();
        var btnTrig1   = new Dictionary<Xbox360Button, bool>();
        var btnTrig2   = new Dictionary<Xbox360Button, bool>();

        // one bad line rejects the whole file — throw with line number and content
        void BadMappingEntry(int lineNo, string line, string why) =>
            throw new MappingParseException($"line {lineNo}: {why}  («{line}»)");

        try
        {
            Dictionary<Xbox360Button, Xbox360Button>? currentBtn     = null;
            Dictionary<AnalogSource, AnalogEntry>?    currentAnalog  = null;
            Dictionary<Xbox360Button, bool>?           currentBtnTrig = null;

            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int idx = 0; idx < lines.Length; idx++)
            {
                int     lineNo = idx + 1;
                var     line   = lines[idx].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                if (line.Equals("[Pad1]", StringComparison.OrdinalIgnoreCase))
                {
                    currentBtn = remap1; currentAnalog = analogMap1; currentBtnTrig = btnTrig1;
                    continue;
                }
                if (line.Equals("[Pad2]", StringComparison.OrdinalIgnoreCase))
                {
                    currentBtn = remap2; currentAnalog = analogMap2; currentBtnTrig = btnTrig2;
                    continue;
                }

                if (currentBtn == null)
                {
                    BadMappingEntry(lineNo, line, "line before any section header");
                    continue;
                }

                var parts = line.Split('=');
                if (parts.Length != 2)
                {
                    BadMappingEntry(lineNo, line, "malformed line, expected 'src = dst'");
                    continue;
                }

                var srcName = parts[0].Trim();
                var dstName = parts[1].Trim();

                // ── analog source ─────────────────────────────────────────────
                if (TryParseAnalogSource(srcName, out var analogSrc))
                {
                    // block
                    if (dstName.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        currentAnalog![analogSrc] = new AnalogEntry { Kind = AnalogTargetKind.Block };
                        continue;
                    }

                    // swap — trigger↔trigger or stick↔stick only
                    if (TryParseAnalogSource(dstName, out var analogDst))
                    {
                        bool srcIsTrigger = analogSrc == AnalogSource.LT || analogSrc == AnalogSource.RT;
                        bool dstIsTrigger = analogDst == AnalogSource.LT || analogDst == AnalogSource.RT;

                        if (srcIsTrigger != dstIsTrigger)
                        {
                            BadMappingEntry(lineNo, line, "cannot mix trigger and stick in swap");
                            continue;
                        }

                        currentAnalog![analogSrc] = new AnalogEntry { Kind = AnalogTargetKind.Swap };
                        continue;
                    }

                    // trigger → digital button (LT = A) — triggers only
                    bool isTrigger = analogSrc == AnalogSource.LT || analogSrc == AnalogSource.RT;
                    if (isTrigger && ButtonNames.TryParse(dstName, out var dstBtn) && dstBtn != Xbox360Button.Guide)
                    {
                        currentAnalog![analogSrc] = new AnalogEntry { Kind = AnalogTargetKind.Button, Button = dstBtn };
                        continue;
                    }

                    BadMappingEntry(lineNo, line, "invalid analog mapping");
                    continue;
                }

                // ── digital button source ────────────────────────────────
                if (!ButtonNames.TryParse(srcName, out var srcBtn))
                {
                    BadMappingEntry(lineNo, line, $"unknown source '{srcName}'");
                    continue;
                }

                if (srcBtn == Xbox360Button.Guide)
                {
                    BadMappingEntry(lineNo, line, "'none' cannot be used as a source");
                    continue;
                }

                // button → trigger (A = LT)
                if (dstName.Equals("LT", StringComparison.OrdinalIgnoreCase))
                {
                    currentBtnTrig![srcBtn] = true;
                    continue;
                }
                if (dstName.Equals("RT", StringComparison.OrdinalIgnoreCase))
                {
                    currentBtnTrig![srcBtn] = false;
                    continue;
                }

                // regular button remap / block
                if (!ButtonNames.TryParse(dstName, out var dstBtn2))
                {
                    BadMappingEntry(lineNo, line, $"unknown destination '{dstName}'");
                    continue;
                }

                currentBtn[srcBtn] = dstBtn2;
            }

            int total = remap1.Count + remap2.Count + analogMap1.Count + analogMap2.Count +
                        btnTrig1.Count + btnTrig2.Count;
            Log.Info($"mapping.ini loaded — {total} mapping(s) active.");
        }
        catch (MappingParseException)
        {
            throw; // rejection — caller surfaces it, keeps old mappings
        }
        catch (Exception ex)
        {
            Log.Exception("MappingConfig.Load", ex);
            throw new MappingParseException(ex.Message);
        }

        return MakeResult(remap1, remap2, analogMap1, analogMap2, btnTrig1, btnTrig2);
    }

    // Enum.TryParse also accepts the NUMERIC value of an enum member — for
    // AnalogSource that means "0".."3" parse as LT/RT/LStick/RStick, so a line
    // like 'LT = 1' would silently load as a trigger swap. Names only: reject
    // anything starting with a digit or sign before the enum parse.
    static bool TryParseAnalogSource(string name, out AnalogSource src)
    {
        src = default;
        if (name.Length == 0 || char.IsDigit(name[0]) || name[0] is '-' or '+')
            return false;
        return Enum.TryParse(name, ignoreCase: true, out src);
    }

    static (ButtonMapping, ButtonMapping, AnalogMapping, AnalogMapping) MakeResult(
        Dictionary<Xbox360Button, Xbox360Button> r1,
        Dictionary<Xbox360Button, Xbox360Button> r2,
        Dictionary<AnalogSource, AnalogEntry>    a1,
        Dictionary<AnalogSource, AnalogEntry>    a2,
        Dictionary<Xbox360Button, bool>          bt1,
        Dictionary<Xbox360Button, bool>          bt2) =>
        (new ButtonMapping(r1), new ButtonMapping(r2),
         new AnalogMapping(a1, bt1), new AnalogMapping(a2, bt2));

    // create mapping.ini with template if it does not exist
    public static void EnsureExists()
    {
        if (File.Exists(ConfigPath)) return;
        WriteDefaultTemplate();
    }

    // (re)write the default template — used by EnsureExists, the Shift+click
    // reset, and when the file on disk lost its sections (emptied raw edit),
    // so the [Pad1]/[Pad2] headers and the guide comments are always restored
    public static void WriteDefaultTemplate()
    {
        try
        {
            File.WriteAllText(ConfigPath,
                "# ViPadLinker button mapping\n" +
                "# Digital buttons: A B X Y LB RB LS RS Start Back Up Down Left Right\n" +
                "# Analog sources:  LT RT LStick RStick\n" +
                "#\n" +
                "# Examples:\n" +
                "#   A = B            remap button A to B\n" +
                "#   X = none         block button X\n" +
                "#   LT = RT          swap triggers\n" +
                "#   LStick = none    block left stick\n" +
                "#   LStick = RStick  swap sticks\n" +
                "#   LT = A           left trigger fires button A (any non-zero press)\n" +
                "#   A = LT           button A sends full left trigger press\n\n" +
                "# NOTE: in SPLIT mode, analog swap acts as block (= none)\n" +
                "[Pad1]\n\n" +
                "[Pad2]\n");
            Log.Info("mapping.ini written with the default template.");
        }
        catch (Exception ex)
        {
            Log.Exception("MappingConfig.EnsureExists", ex);
            Log.Warn("Could not create mapping.ini - edit manually if needed.");
        }
    }
}
