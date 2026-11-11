using System.Runtime.InteropServices;

namespace LocalDeck.App.Services;

public sealed class PointerService
{
    private const uint SpiGetMouseSpeed = 0x0070;
    private const uint SpiSetMouseSpeed = 0x0071;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    public int GetSpeed()
    {
        var speed = 10;
        _ = SystemParametersInfoRef(SpiGetMouseSpeed, 0, ref speed, 0);
        return Math.Clamp(speed, 1, 20);
    }

    public bool SetSpeed(int speed)
    {
        speed = Math.Clamp(speed, 1, 20);
        return SystemParametersInfoValue(
            SpiSetMouseSpeed, 0, (IntPtr)speed, SpifUpdateIniFile | SpifSendChange);
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfoRef(
        uint action, uint parameter, ref int value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfoValue(
        uint action, uint parameter, IntPtr value, uint flags);
}
