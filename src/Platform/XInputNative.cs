using System.Runtime.InteropServices;

// ─── XInput structs ───────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
struct XINPUT_GAMEPAD
{
    public ushort wButtons;
    public byte   bLeftTrigger;
    public byte   bRightTrigger;
    public short  sThumbLX;
    public short  sThumbLY;
    public short  sThumbRX;
    public short  sThumbRY;
}

[StructLayout(LayoutKind.Sequential)]
struct XINPUT_STATE
{
    public uint           dwPacketNumber;
    public XINPUT_GAMEPAD Gamepad;
}

[StructLayout(LayoutKind.Sequential)]
struct XINPUT_VIBRATION
{
    public ushort wLeftMotorSpeed;
    public ushort wRightMotorSpeed;
}

// ─── XInput P/Invoke wrapper ──────────────────────────────────────────────────

static class XInput
{
    const string DLL           = "xinput1_4.dll";
    const uint   ERROR_SUCCESS = 0;

    [DllImport(DLL)] static extern uint XInputGetState(uint index, out XINPUT_STATE state);
    [DllImport(DLL)] static extern uint XInputSetState(uint index, ref XINPUT_VIBRATION vib);

    public static bool GetState(uint index, out XINPUT_STATE state) =>
        XInputGetState(index, out state) == ERROR_SUCCESS;

    public static void SetVibration(uint index, byte large, byte small)
    {
        var vib = new XINPUT_VIBRATION
        {
            wLeftMotorSpeed  = (ushort)(large / 255.0 * 65535),
            wRightMotorSpeed = (ushort)(small / 255.0 * 65535)
        };
        XInputSetState(index, ref vib);
    }

    public static void StopVibration(uint index) => SetVibration(index, 0, 0);

    public static int OccupiedSlotCount
    {
        get
        {
            int count = 0;
            for (uint i = 0; i < 4; i++)
                if (GetState(i, out _)) count++;
            return count;
        }
    }

    public static bool AllSlotsFull => OccupiedSlotCount >= 4;
}
