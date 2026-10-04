#Requires -Version 7.0
<#
.SYNOPSIS
    Detects and reports all connected XInput gamepad controllers.
.EXAMPLE
    .\Get-XInputGamepads.ps1               # one-shot list
    .\Get-XInputGamepads.ps1 -Monitor      # live polling
    .\Get-XInputGamepads.ps1 -Diagnostics  # debug why nothing shows
#>
[CmdletBinding()]
param(
    [switch]$Monitor,
    [int]$PollIntervalMs = 100,
    [switch]$Diagnostics
)

# ---------------------------------------------------------------------------
# Resolve the DLL — use the full System32 path so PS7 finds it reliably
# ---------------------------------------------------------------------------
$dllCandidates = @(
    "$env:SystemRoot\System32\xinput1_4.dll"
    "$env:SystemRoot\System32\xinput1_3.dll"
    "$env:SystemRoot\System32\xinput1_2.dll"
    "$env:SystemRoot\System32\xinput1_1.dll"
    "$env:SystemRoot\System32\xinput9_1_0.dll"
)

$dllPath = $dllCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($Diagnostics) {
    Write-Host "`n[Diagnostics]" -ForegroundColor Cyan
    Write-Host "  PowerShell : $($PSVersionTable.PSVersion)"
    Write-Host "  64-bit proc: $([Environment]::Is64BitProcess)"
    Write-Host "  DLL candidates:"
    foreach ($c in $dllCandidates) {
        $color = if (Test-Path $c) { 'Green' } else { 'DarkGray' }
        Write-Host ("    {0,-55} {1}" -f $c, $(if (Test-Path $c) { 'FOUND' } else { 'missing' })) -ForegroundColor $color
    }
    Write-Host "  Using: $(if ($dllPath) { $dllPath } else { 'NONE' })`n"
}

if (-not $dllPath) {
    Write-Error "No XInput DLL found in System32. Install the DirectX runtime or a controller driver."
    exit 1
}

# ---------------------------------------------------------------------------
# Compile P/Invoke wrapper.
# Use a namespace derived from the DLL name so re-running in the same PS7
# session never tries to redefine an already-compiled type.
# ---------------------------------------------------------------------------
$ns = "XInput_" + [IO.Path]::GetFileNameWithoutExtension($dllPath) -replace '[^A-Za-z0-9]', '_'

if (-not ([System.Management.Automation.PSTypeName]"${ns}.Api").Type) {

    # Escape backslashes for the C# string literal
    $dllEscaped = $dllPath.Replace('\', '\\')

    $src = @"
using System;
using System.Runtime.InteropServices;

namespace $ns {

    [StructLayout(LayoutKind.Sequential)]
    public struct GAMEPAD {
        public ushort wButtons;
        public byte   bLeftTrigger;
        public byte   bRightTrigger;
        public short  sThumbLX;
        public short  sThumbLY;
        public short  sThumbRX;
        public short  sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STATE {
        public uint    dwPacketNumber;
        public GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BATTERY_INFO {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CAPABILITIES {
        public byte    Type;
        public byte    SubType;
        public ushort  Flags;
        public GAMEPAD Gamepad;
        public ushort  VibrationWL;
        public ushort  VibrationWR;
    }

    public static class Api {
        [DllImport("$dllEscaped", EntryPoint = "XInputGetState")]
        public static extern uint GetState(uint index, ref STATE state);

        [DllImport("$dllEscaped", EntryPoint = "XInputGetBatteryInformation")]
        public static extern uint GetBatteryInformation(uint index, byte devType, ref BATTERY_INFO info);

        [DllImport("$dllEscaped", EntryPoint = "XInputGetCapabilities")]
        public static extern uint GetCapabilities(uint index, uint flags, ref CAPABILITIES caps);
    }
}
"@

    try {
        Add-Type -TypeDefinition $src -Language CSharp -ErrorAction Stop
        if ($Diagnostics) { Write-Host "[Diagnostics] P/Invoke compiled OK`n" -ForegroundColor Green }
    } catch {
        Write-Error "Failed to compile P/Invoke wrapper: $_"
        exit 1
    }

} elseif ($Diagnostics) {
    Write-Host "[Diagnostics] P/Invoke type already loaded in this session (namespace: $ns)`n" -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
$OK            = 0
$NOT_CONNECTED = 1167

$BATTERY_TYPE  = @{ 0='Disconnected'; 1='Wired'; 2='AA'; 3='NiMH' }
$BATTERY_LEVEL = @{ 0='Empty'; 1='Low'; 2='Medium'; 3='Full' }
$SUBTYPE       = @{ 0='Unknown'; 1='Gamepad'; 2='Wheel'; 3='ArcadeStick';
                    4='FlightStick'; 5='DancePad'; 6='Guitar'; 8='DrumKit'; 0x13='ArcadePad' }

$BUTTON_MAP = [ordered]@{
    0x0001='DPad_Up';  0x0002='DPad_Down'; 0x0004='DPad_Left'; 0x0008='DPad_Right'
    0x0010='Start';    0x0020='Back';      0x0040='LS_Click';   0x0080='RS_Click'
    0x0100='LB';       0x0200='RB';        0x1000='A';          0x2000='B'
    0x4000='X';        0x8000='Y'
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Get-PressedButtons([ushort]$w) {
    $list = foreach ($kv in $BUTTON_MAP.GetEnumerator()) {
        if ($w -band $kv.Key) { $kv.Value }
    }
    if (-not $list) { return 'None' }
    return ($list -join ', ')
}

function Normalize-Axis([short]$v)   { [math]::Round($(if ($v -ge 0) { $v / 32767.0 } else { $v / 32768.0 }), 3) }
function Normalize-Trigger([byte]$v) { [math]::Round($v / 255.0, 3) }

# ---------------------------------------------------------------------------
# Core query — called once per slot per poll cycle
# ---------------------------------------------------------------------------
function Get-GamepadInfo([uint32]$index) {
    $api    = ($ns + '.Api') -as [type]
    $state  = New-Object ($ns + '.STATE')
    $result = $api::GetState($index, [ref]$state)

    if ($Diagnostics) {
        $label = switch ($result) {
            $OK            { 'OK' }
            $NOT_CONNECTED { 'NOT_CONNECTED' }
            default        { "ERROR_0x$('{0:X}' -f $result)" }
        }
        Write-Host ("  [Diagnostics] Slot {0}  GetState() = {1} ({2})" -f $index, $result, $label) -ForegroundColor DarkGray
    }

    if ($result -eq $NOT_CONNECTED) { return $null }
    if ($result -ne $OK) {
        Write-Warning "Slot $index — unexpected return code $result (0x$('{0:X}' -f $result))"
        return $null
    }

    $caps = New-Object ($ns + '.CAPABILITIES')
    $api::GetCapabilities($index, 1, [ref]$caps) | Out-Null
    $sub = if ($SUBTYPE.ContainsKey([int]$caps.SubType)) { $SUBTYPE[[int]$caps.SubType] } else { "0x$('{0:X2}' -f $caps.SubType)" }

    $batt  = New-Object ($ns + '.BATTERY_INFO')
    $api::GetBatteryInformation($index, 0, [ref]$batt) | Out-Null
    $bType = if ($BATTERY_TYPE.ContainsKey([int]$batt.BatteryType))   { $BATTERY_TYPE[[int]$batt.BatteryType]   } else { 'Unknown' }
    $bLvl  = if ($BATTERY_LEVEL.ContainsKey([int]$batt.BatteryLevel)) { $BATTERY_LEVEL[[int]$batt.BatteryLevel] } else { 'Unknown' }

    $gp = $state.Gamepad
    [PSCustomObject]@{
        SlotIndex    = $index
        Connected    = $true
        DLL          = [IO.Path]::GetFileName($dllPath)
        SubType      = $sub
        PacketNumber = $state.dwPacketNumber
        BatteryType  = $bType
        BatteryLevel = $bLvl
        Buttons      = Get-PressedButtons $gp.wButtons
        LeftTrigger  = Normalize-Trigger  $gp.bLeftTrigger
        RightTrigger = Normalize-Trigger  $gp.bRightTrigger
        LeftStick_X  = Normalize-Axis     $gp.sThumbLX
        LeftStick_Y  = Normalize-Axis     $gp.sThumbLY
        RightStick_X = Normalize-Axis     $gp.sThumbRX
        RightStick_Y = Normalize-Axis     $gp.sThumbRY
        RawButtons   = '0x{0:X4}' -f $gp.wButtons
    }
}

function Get-AllGamepads {
    0..3 | ForEach-Object { Get-GamepadInfo ([uint32]$_) } | Where-Object { $_ -ne $null }
}

# ---------------------------------------------------------------------------
# Entry points
# ---------------------------------------------------------------------------
if ($Monitor) {
    Write-Host "Monitoring XInput gamepads — Ctrl+C to stop`n" -ForegroundColor Cyan
    $prev = @{}
    while ($true) {
        $now  = Get-Date -Format 'HH:mm:ss.fff'
        $pads = @(Get-AllGamepads)
        if ($pads.Count -eq 0) {
            Write-Host "[$now] No controllers detected." -ForegroundColor DarkGray
        } else {
            foreach ($p in $pads) {
                $sig = "$($p.PacketNumber)|$($p.RawButtons)"
                if ($prev[$p.SlotIndex] -ne $sig) {
                    $prev[$p.SlotIndex] = $sig
                    Write-Host ("[$now] Slot {0} | Buttons: {1,-30} | LT:{2:F2} RT:{3:F2} | LS:({4,6},{5,6}) RS:({6,6},{7,6})" -f `
                        $p.SlotIndex, $p.Buttons,
                        $p.LeftTrigger, $p.RightTrigger,
                        $p.LeftStick_X, $p.LeftStick_Y,
                        $p.RightStick_X, $p.RightStick_Y) -ForegroundColor Yellow
                }
            }
        }
        Start-Sleep -Milliseconds $PollIntervalMs
    }
} else {
    if ($Diagnostics) { Write-Host "[Diagnostics] Querying all 4 slots...`n" -ForegroundColor Cyan }
    $pads = @(Get-AllGamepads)
    if ($pads.Count -eq 0) {
        Write-Host "No XInput gamepads detected." -ForegroundColor Yellow
        Write-Host "Tip: run with -Diagnostics to see raw API return codes per slot." -ForegroundColor DarkGray
    } else {
        Write-Host "Found $($pads.Count) connected XInput gamepad(s):`n" -ForegroundColor Green
        $pads | Format-List
    }
}