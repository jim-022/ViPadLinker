#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Interactive test rig: add / remove virtual XInput pads from the console.

.DESCRIPTION
    Replaces the old console app's pad-holding workflow for testing the tray.
    Opens a key loop:

        A - add a virtual pad   (holds the next free XInput slot)
        R - remove a pad        (releases the most recently added)
        Q - quit                (releases every pad)

    The pads live ONLY while this script runs: quitting (or Ctrl+C, or closing
    the window) releases them. Run it in one terminal and watch the ViPadLinker
    tray in another — the pads this tool holds appear to the tray as PHYSICAL
    pads on the free slots, so you can exercise the slot map / watcher / status
    line without real hardware.

    Uses the same Nefarius.ViGEm.Client.dll the app builds against
    (bin\release), falling back to the NuGet cache. Requires the ViGEmBus
    driver installed (same as the app).

.EXAMPLE
    ./tools/Simulate-Pads.ps1
    ./tools/Simulate-Pads.ps1 -DllPath C:\somewhere\Nefarius.ViGEm.Client.dll
#>

param([string]$DllPath)

function Find-ViPadDll {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    foreach ($c in @(
        (Join-Path $repoRoot 'bin\release\Nefarius.ViGEm.Client.dll'),
        (Join-Path $repoRoot 'bin\Debug\net48\Nefarius.ViGEm.Client.dll'))) {
        if (Test-Path $c) { return (Resolve-Path $c).Path }
    }
    $cache = Join-Path $HOME '.nuget\packages\nefarius.vigem.client'
    if (Test-Path $cache) {
        $hit = Get-ChildItem $cache -Recurse -Filter 'Nefarius.ViGEm.Client.dll' |
               Where-Object { $_.FullName -match 'netstandard' } |
               Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    throw "Nefarius.ViGEm.Client.dll not found. Run .\BUILD.bat first, or pass -DllPath <path>."
}

# ── connect ───────────────────────────────────────────────────────────────────
$dll = if ($DllPath) { $DllPath } else { Find-ViPadDll }
Add-Type -Path $dll

try {
    $client = New-Object Nefarius.ViGEm.Client.ViGEmClient
} catch {
    Write-Host "Could not connect to ViGEmBus. Install the driver:" -ForegroundColor Red
    Write-Host "  https://github.com/nefarius/ViGEmBus/releases" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor DarkGray
    exit 1
}

$pads = New-Object 'System.Collections.Generic.List[object]'

function SlotOf($pad) { try { $pad.UserIndex + 1 } catch { '?' } }

function Show-Status {
    if ($pads.Count -eq 0) {
        Write-Host ("  Held: (none)          [max 4 slots]")
    } else {
        $slots = ($pads | ForEach-Object { SlotOf $_ }) -join ', '
        Write-Host ("  Held: slot {0}   ({1} pad(s))" -f $slots, $pads.Count)
    }
}

function Add-Pad {
    if ($pads.Count -ge 4) { Write-Host "  No free XInput slot (max 4)." -ForegroundColor Yellow; return }
    try {
        $pad = $client.CreateXbox360Controller()
        $pad.Connect()
        $pads.Add($pad)
        Write-Host ("  + ViPad on slot {0}" -f (SlotOf $pad)) -ForegroundColor Green
    } catch {
        Write-Host "  Add failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

function Remove-Pad {
    if ($pads.Count -eq 0) { Write-Host "  No pads to remove." -ForegroundColor Yellow; return }
    $pad  = $pads[$pads.Count - 1]
    $slot = SlotOf $pad
    try { $pad.Disconnect() } catch { }
    try { ($pad -as [IDisposable]).Dispose() } catch { }   # frees the native ViGEm handle
    $pads.RemoveAt($pads.Count - 1)
    Write-Host ("  - ViPad released (slot {0})" -f $slot) -ForegroundColor Cyan
}

# ── run loop ──────────────────────────────────────────────────────────────────
try {
    Write-Host "ViPadLinker pad simulator  (ViGEm client: $dll)" -ForegroundColor White
    Write-Host "  A = add pad    R = remove pad    Q = quit" -ForegroundColor White
    Write-Host ""

    while ($true) {
        Show-Status
        $key = [Console]::ReadKey($true).Key
        switch ($key) {
            'A' { Add-Pad }
            'R' { Remove-Pad }
            'Q' { return }
        }
    }
}
finally {
    # Runs on Q, on Ctrl+C, and on any error — and even if this block were
    # skipped, process exit closes the handles. Belt and braces.
    while ($pads.Count -gt 0) {
        $pad = $pads[$pads.Count - 1]
        try { $pad.Disconnect() } catch { }
        try { ($pad -as [IDisposable]).Dispose() } catch { }
        $pads.RemoveAt($pads.Count - 1)
    }
    try { ($client -as [IDisposable]).Dispose() } catch { }
    Write-Host "`nAll pads released." -ForegroundColor DarkGray
}
