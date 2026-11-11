using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LocalDeck.App.Services;

public static class Dpapi
{
    private const int CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] data)
    {
        return Transform(data, protect: true);
    }

    public static byte[] Unprotect(byte[] data)
    {
        return Transform(data, protect: false);
    }

    private static byte[] Transform(byte[] data, bool protect)
    {
        if (data.Length == 0)
        {
            return [];
        }

        var input = new DataBlob();
        var output = new DataBlob();
        input.Data = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, input.Data, data.Length);
        input.Size = data.Length;

        try
        {
            var success = protect
                ? CryptProtectData(ref input, "LocalDeck", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref output);

            if (!success)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(input.Data);
            }

            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        IntPtr entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
