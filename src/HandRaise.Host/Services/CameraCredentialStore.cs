using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HandRaise.Host.Services;

public interface IDataProtector
{
    byte[] Protect(byte[] value);
    byte[] Unprotect(byte[] value);
}

public sealed class MachineDpapiProtector : IDataProtector
{
    private const int LocalMachine = 0x4;
    private const int UiForbidden = 0x1;

    public byte[] Protect(byte[] value) => Transform(value, protect: true);
    public byte[] Unprotect(byte[] value) => Transform(value, protect: false);

    private static byte[] Transform(byte[] value, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI requiere Windows.");
        var input = ToBlob(value);
        try
        {
            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, LocalMachine | UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI no pudo procesar las credenciales.");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }

    private static DataBlob ToBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new(bytes.Length, pointer);
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct DataBlob(int Size, IntPtr Data);
    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("Crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("Kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

public sealed class JsonCameraCredentialStore(string path, IDataProtector protector) : ICameraCredentialStore, IDisposable
{
    private readonly string _path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<CameraCredentials?> GetAsync(string cameraId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var values = await ReadAsync(token);
            if (!values.TryGetValue(cameraId, out var protectedValue)) return null;
            var json = protector.Unprotect(Convert.FromBase64String(protectedValue));
            return JsonSerializer.Deserialize<CameraCredentials>(json);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(string cameraId, CameraCredentials credentials, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var values = await ReadAsync(token);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(credentials);
            values[cameraId] = Convert.ToBase64String(protector.Protect(bytes));
            await WriteAsync(values, token);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string cameraId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var values = await ReadAsync(token);
            if (values.Remove(cameraId)) await WriteAsync(values, token);
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: token)
            ?? new(StringComparer.OrdinalIgnoreCase);
    }

    private async Task WriteAsync(Dictionary<string, string> values, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, values, cancellationToken: token);
        File.Move(temporary, _path, true);
    }

    public void Dispose() => _gate.Dispose();
}
