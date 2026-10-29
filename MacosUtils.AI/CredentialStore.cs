using System.Runtime.InteropServices;
using System.Text;

namespace MacosUtils.AI;

/// <summary>
/// Keeps the AI endpoint's API key in the user's login Keychain (Security.framework generic
/// password items) instead of a file. One item per endpoint host: one endpoint's key must never be
/// loaded for (and thus sent to) a different endpoint. macOS only; elsewhere nothing is stored.
/// </summary>
/// <remarks>
/// The SecKeychain* calls are the classic C API: deprecated in favour of SecItem*, but still
/// supported and far simpler to call without a CoreFoundation binding. The item is visible in
/// Keychain Access as "MacosUtils AI Chat" with the host as its account.
/// </remarks>
public static class CredentialStore
{
    private const string Service = "MacosUtils AI Chat";
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;

    public static bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>Saves the key for an endpoint's host. Throws when the host is unknown: an
    /// unbound key could later be sent to whatever endpoint happens to be typed next.</summary>
    public static void Save(string secret, string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("An API key can only be saved together with the endpoint host it belongs to.", nameof(host));
        if (!IsSupported)
            throw new PlatformNotSupportedException("The API key can only be saved in the macOS Keychain.");

        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(Account(host));
        var password = Encoding.UTF8.GetBytes(secret);
        try
        {
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                out _, IntPtr.Zero, out var item);
            if (status == ErrSecSuccess)
            {
                try
                {
                    Check(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password), "update");
                }
                finally
                {
                    CFRelease(item);
                }
                return;
            }
            if (status != ErrSecItemNotFound)
                Check(status, "read");

            Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                (uint)password.Length, password, out var added), "save");
            if (added != IntPtr.Zero)
                CFRelease(added);
        }
        finally
        {
            Array.Clear(password);
        }
    }

    /// <summary>Reads the key saved for this endpoint's host — never another host's. Null when none.</summary>
    public static string? Load(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || !IsSupported)
            return null;
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(Account(host));
        IntPtr data = IntPtr.Zero;
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
            out var length, ref data, IntPtr.Zero);
        if (status != ErrSecSuccess || data == IntPtr.Zero)
            return null;
        var bytes = new byte[length];
        try
        {
            Marshal.Copy(data, bytes, 0, bytes.Length);
            var secret = Encoding.UTF8.GetString(bytes);
            return secret.Length > 0 ? secret : null;
        }
        finally
        {
            Array.Clear(bytes);
            SecKeychainItemFreeContent(IntPtr.Zero, data);
        }
    }

    /// <summary>Deletes the key saved for this endpoint's host, if any.</summary>
    public static void Delete(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || !IsSupported)
            return;
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(Account(host));
        if (SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                out _, IntPtr.Zero, out var item) != ErrSecSuccess)
            return;
        try
        {
            SecKeychainItemDelete(item);
        }
        finally
        {
            CFRelease(item);
        }
    }

    private static string Account(string host) => host.Trim().ToLowerInvariant();

    private static void Check(int status, string action)
    {
        if (status != ErrSecSuccess)
            throw new InvalidOperationException($"Could not {action} the API key in the Keychain (OSStatus {status}).");
    }

    // Two shapes of the same call: one that returns the password data, one that returns the item.
    [DllImport(SecurityFramework)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength, ref IntPtr passwordData, IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength, IntPtr passwordData, out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}
