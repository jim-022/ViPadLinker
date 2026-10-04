# Generator for ViPadLinker UI sounds.
# Produces small mono 16-bit PCM WAVs (playable by System.Media.SoundPlayer)
# into assets\sounds\ - the csproj embeds those into the exe.
# Tweak the tone lists below and re-run to taste.

$ErrorActionPreference = 'Stop'
$rate = 44100
$amp  = 0.45   # keep headroom, these play at system volume

function Render-Tones($tones) {
    # each tone: @(freqHz, durationMs) - sine, 6 ms attack, exponential decay, 10 ms fade-out
    $samples = New-Object 'System.Collections.Generic.List[double]'
    foreach ($t in $tones) {
        $freq = $t[0]; $durMs = $t[1]
        $n      = [int]($rate * $durMs / 1000)
        $attack = [int]($rate * 0.006)
        $fadeN  = [int]($rate * 0.010)
        $tau    = [double]$n / 5.0        # decays to ~1 % by end of tone
        for ($i = 0; $i -lt $n; $i++) {
            $env = [Math]::Exp(-$i / $tau)
            if ($i -lt $attack)     { $env *= ($i / $attack) }
            if ($i -gt $n - $fadeN) { $env *= [double](($n - $i) / $fadeN) }
            $samples.Add([Math]::Sin(2 * [Math]::PI * $freq * ($i / $rate)) * $env * $amp)
        }
    }
    return ,$samples
}

function Write-Wav([string]$path, [System.Collections.Generic.List[double]]$samples) {
    $n = $samples.Count
    $dataBytes = $n * 2
    $bw = New-Object System.IO.BinaryWriter([System.IO.File]::Create($path))
    $ascii = [System.Text.Encoding]::ASCII
    $bw.Write($ascii.GetBytes('RIFF'))
    $bw.Write([int32](36 + $dataBytes))
    $bw.Write($ascii.GetBytes('WAVE'))
    $bw.Write($ascii.GetBytes('fmt '))
    $bw.Write([int32]16)            # fmt chunk size
    $bw.Write([int16]1)             # PCM
    $bw.Write([int16]1)             # mono
    $bw.Write([int32]$rate)
    $bw.Write([int32]($rate * 2))   # byte rate
    $bw.Write([int16]2)             # block align
    $bw.Write([int16]16)            # bits per sample
    $bw.Write($ascii.GetBytes('data'))
    $bw.Write([int32]$dataBytes)
    foreach ($s in $samples) {
        $v = [Math]::Max(-1.0, [Math]::Min(1.0, $s))
        $bw.Write([int16]($v * 32767))
    }
    $bw.Close()
}

$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\sounds'

# connect: rising two-note "hello"  (E5 -> A5)
$connectTones = @(@(659.3, 130), @(880.0, 300))
$connect = Render-Tones $connectTones
Write-Wav (Join-Path $out 'connect.wav') $connect

# disconnect: falling two-note "bye" (G5 -> D5, lower + softer)
$disconnectTones = @(@(784.0, 110), @(587.3, 340))
$disconnect = Render-Tones $disconnectTones
Write-Wav (Join-Path $out 'disconnect.wav') $disconnect

Get-ChildItem $out -Filter *.wav |
    Select-Object Name, @{n='KB';e={[Math]::Round($_.Length/1KB,1)}}
