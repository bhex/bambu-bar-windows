using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BambuBar.Core.Store;

/// <summary>
/// The printer's LAN access code, kept in the Windows Credential Manager per serial number.
/// (The macOS original uses the login keychain; on Windows the equivalent store is the Credential
/// Manager, where the value is encrypted at rest with the user's logon key and is not readable by
/// other users or processes without the same logon.)
/// </summary>
public static class CredentialStore
{
    private const string TargetPrefix = "BambuBar.access-code.";

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWriteW(ref CREDENTIALA credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredReadW(string target, uint type, int reserved, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDeleteW(string target, uint type, int reserved);

    [DllImport("kernel32.dll")]
    private static extern void LocalFree(IntPtr pointer);

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    /// <summary>
    /// The CREDENTIALA layout as exported by advapi32 (wide-string variant). Field order and sizes must
    /// match exactly or the API reads garbage.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIALA
    {
        public uint CredentialType;
        public uint Flags;
        public uint Persist;
        public uint LastWritten;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string Comment;
        public IntPtr CredentialBlob;
        public uint CredentialBlobSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetAlias;
        public uint AttributeCount;
        public IntPtr CredentialAttributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
        [MarshalAs(UnmanagedType.LPWStr)] public string Provider;
    }

    public static string? AccessCode(string serial)
    {
        if (!CredReadW(Target(serial), CredTypeGeneric, 0, out var ptr)) return null;
        try
        {
            // Read fields manually at known offsets to avoid PtrToStructure
            // crashing on null/invalid string pointers in the struct.
            // Layout confirmed on this machine (Windows 10 26300, 64-bit):
            //   Offset 32: BlobSize (DWORD, bytes, UTF-16 data)
            //   Offset 40: CredentialBlob pointer
            //   Data is UTF-16LE.
            var blobPtr = Marshal.ReadIntPtr(ptr, 40);
            if (blobPtr == IntPtr.Zero)
                return null;

            var blobSize = Marshal.ReadInt32(ptr, 32);
            if (blobSize > 0 && blobSize <= 1_048_576)
            {
                var bytes = new byte[blobSize];
                Marshal.Copy(blobPtr, bytes, 0, blobSize);
                // Decode as UTF-16LE (cmdkey stores wide strings)
                return Encoding.Unicode.GetString(bytes);
            }

            // Fallback: read to null terminator
            var s = Marshal.PtrToStringUni(blobPtr);
            return s;
        }
        catch
        {
            return null;
        }
        finally
        {
            LocalFree(ptr);
        }
    }

    /// <summary>
    /// Attempts to persist the access code. Returns true on success.
    /// Tries CredWriteW first; if that fails (e.g. DMZ policy blocking direct API),
    /// falls back to <c>cmdkey /generic:... /user:... /pass:...</c> which is known to work.
    /// Never throws — caller decides how to surface the failure.
    /// </summary>
    public static bool SetAccessCode(string code, string serial)
    {
        // --- Primary: direct CredWriteW ---
        try
        {
            var blob = Encoding.UTF8.GetBytes(code);
            var blobPtr = Marshal.AllocHGlobal(blob.Length);
            try
            {
                Marshal.Copy(blob, 0, blobPtr, blob.Length);
                var credential = new CREDENTIALA
                {
                    CredentialType = CredTypeGeneric,
                    Persist = CredPersistLocalMachine,
                    TargetName = Target(serial),
                    UserName = "bblp",
                    CredentialBlob = blobPtr,
                    CredentialBlobSize = (uint)blob.Length,
                };
                if (CredWriteW(ref credential, 0))
                    return true;
                // Fall through to cmdkey fallback
            }
            finally
            {
                Marshal.FreeHGlobal(blobPtr);
            }
        }
        catch
        {
            // P/Invoke or marshal errors — fall through to cmdkey.
        }

        // --- Fallback: cmdkey (works on this machine) ---
        try
        {
            var target = Target(serial);
            var args = "/generic:" + QuoteArg(target) + " /user:bblp /pass:" + QuoteArg(code);
            var psi = new ProcessStartInfo
            {
                FileName = "cmdkey",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(10_000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes the stored access code. Returns true on success. Never throws.</summary>
    public static bool RemoveAccessCode(string serial)
    {
        if (CredDeleteW(Target(serial), CredTypeGeneric, 0))
            return true;
        // Fallback: cmdkey /delete
        try
        {
            var args = "/delete:" + QuoteArg(Target(serial));
            var psi = new ProcessStartInfo
            {
                FileName = "cmdkey",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10_000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string QuoteArg(string s)
    {
        if (s.IndexOf(' ') < 0 && s.IndexOf('"') < 0) return s;
        return '"' + s + '"';
    }

    private static string Target(string serial) => TargetPrefix + serial;
}