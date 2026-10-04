using System.Threading;

// ─── Sound ────────────────────────────────────────────────────────────────────
// Plays embedded WAV chimes (assets/sounds/*.wav, embedded by the app build —
// see src/ViPadLinker.csproj). Assemblies without the resources stay silent.
// Each sound plays on its own short-lived background thread. Regenerate the
// WAVs with tools/Make-Sounds.ps1.

static class Sound
{
    // Logical resource names of the embedded chimes.
    const string WavConnect    = "connect.wav";
    const string WavDisconnect = "disconnect.wav";

    // pad connected / reconnected — rising two-note chime
    public static void Connected() => Play(WavConnect);

    // pad disconnected — falling two-note chime (alert)
    public static void Disconnected() => Play(WavDisconnect);

    static void Play(string wavResource)
    {
        new Thread(() =>
        {
            try
            {
                using var stm = System.Reflection.Assembly
                    .GetExecutingAssembly().GetManifestResourceStream(wavResource);
                if (stm == null) return; // not embedded in this assembly — stay silent
                using var player = new System.Media.SoundPlayer(stm);
                player.PlaySync(); // dedicated background thread — blocking is fine
            }
            catch { }
        })
        { IsBackground = true }.Start();
    }
}
