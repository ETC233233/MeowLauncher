using System.Runtime.InteropServices;
using System.Text;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 用 Windows DPAPI（当前用户范围）保护要落盘的小块机密，例如代理密码。
/// 存的是 base64 密文，换机器/换用户都解不开，这是期望行为。
/// </summary>
public static class SecretStore
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = Encoding.UTF8.GetBytes(plain);
        var input = new DataBlob { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(bytes, 0, input.pbData, bytes.Length);
            if (!CryptProtectData(ref input, "MeowLauncher", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                return "";
            var encrypted = new byte[output.cbData];
            Marshal.Copy(output.pbData, encrypted, 0, output.cbData);
            return Convert.ToBase64String(encrypted);
        }
        catch { return ""; }
        finally
        {
            if (input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(input.pbData);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return "";
        try
        {
            var bytes = Convert.FromBase64String(protectedBase64);
            var input = new DataBlob { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
            var output = new DataBlob();
            try
            {
                Marshal.Copy(bytes, 0, input.pbData, bytes.Length);
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                    return "";
                var plain = new byte[output.cbData];
                Marshal.Copy(output.pbData, plain, 0, output.cbData);
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                if (input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(input.pbData);
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            }
        }
        catch { return ""; }
    }
}
