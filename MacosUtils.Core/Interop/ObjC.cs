using System.Runtime.InteropServices;

namespace MacosUtils.Core.Interop;

/// <summary>
/// The few Objective-C runtime calls the app needs (NSFileManager, NSURL), without a binding
/// library. objc_msgSend is imported once per signature it is called with.
/// </summary>
internal static class ObjC
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";

    private static readonly Lazy<bool> FoundationLoaded = new(() =>
    {
        // The CLI does not link Foundation; its classes are only found once it is loaded.
        NativeLibrary.Load(Foundation);
        return true;
    });

    [DllImport(LibObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibObjC, EntryPoint = "sel_registerName")]
    public static extern IntPtr Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SendBool(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2, out IntPtr error);

    [DllImport(LibObjC, EntryPoint = "objc_autoreleasePoolPush")]
    private static extern IntPtr PoolPush();

    [DllImport(LibObjC, EntryPoint = "objc_autoreleasePoolPop")]
    private static extern void PoolPop(IntPtr pool);

    public static IntPtr Class(string name)
    {
        _ = FoundationLoaded.Value;
        var cls = GetClass(name);
        return cls != IntPtr.Zero ? cls : throw new InvalidOperationException($"Objective-C class {name} not found.");
    }

    /// <summary>An autoreleased NSString.</summary>
    public static IntPtr String(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Send(Class("NSString"), Selector("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>An autoreleased file NSURL for an absolute path.</summary>
    public static IntPtr FileUrl(string path) =>
        Send(Class("NSURL"), Selector("fileURLWithPath:"), String(path));

    /// <summary>The text of an NSString, or null for nil.</summary>
    public static string? ToManagedString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero)
            return null;
        return Marshal.PtrToStringUTF8(Send(nsString, Selector("UTF8String")));
    }

    /// <summary>localizedDescription of an NSError, or a generic text for nil.</summary>
    public static string ErrorDescription(IntPtr nsError) =>
        nsError == IntPtr.Zero
            ? "unknown error"
            : ToManagedString(Send(nsError, Selector("localizedDescription"))) ?? "unknown error";

    /// <summary>Frees the autoreleased objects created inside it.</summary>
    public readonly struct AutoreleasePool : IDisposable
    {
        private readonly IntPtr _pool;

        public AutoreleasePool() => _pool = PoolPush();

        public void Dispose()
        {
            if (_pool != IntPtr.Zero)
                PoolPop(_pool);
        }
    }
}
