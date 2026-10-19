using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LexiLens.App.Platform;

// DPAPI CurrentUser: keys are usable only by this Windows account, not copied
// into ordinary settings, export files, repository content or diagnostics.
internal sealed class CredentialVault(string directory)
{
    private readonly string _path = Path.Combine(directory, "credentials.dat");
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);

    internal string Read(string scope)
    {
        if (!File.Exists(_path)) return "";
        var encrypted = File.ReadAllBytes(_path);
        var plain = Transform(encrypted, false);
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(plain)?.GetValueOrDefault(scope) ?? ""; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal void Save(string scope, string key)
    {
        Dictionary<string, string> values = new();
        if (File.Exists(_path))
        {
            var existing = Transform(File.ReadAllBytes(_path), false);
            try { values = JsonSerializer.Deserialize<Dictionary<string, string>>(existing) ?? new(); }
            finally { CryptographicOperations.ZeroMemory(existing); }
        }
        if (string.IsNullOrWhiteSpace(key)) values.Remove(scope); else values[scope] = key.Trim();
        var plain = JsonSerializer.SerializeToUtf8Bytes(values);
        try
        {
            var protectedData = Transform(plain, true);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(_path + ".tmp", protectedData);
            File.Move(_path + ".tmp", _path, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal static string Scope(string providerId, Uri endpoint) => providerId + "|" + endpoint.AbsoluteUri;

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var success = protect
                ? CryptProtectData(ref input, "LexiLens provider credentials", 0, 0, 0, 1, out output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!success) throw new Win32Exception("无法读取密钥库。请在原 Windows 账号中运行，或备份后移走 credentials.dat 并重新填写密钥。");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != 0)
            {
                if (!protect) for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
}
