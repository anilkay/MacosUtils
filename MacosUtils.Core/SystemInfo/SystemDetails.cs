using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MacosUtils.Core.SystemInfo;

/// <summary>
/// Machine details that <see cref="Environment"/> alone does not expose on macOS: the macOS
/// version, Mac model, chip, physical and available memory, boot time and the main display.
/// Read from sysctl, vm_stat, SystemVersion.plist and CoreGraphics. Read-only; values that cannot
/// be read come back empty/zero instead of throwing (also on other systems).
/// </summary>
public static partial class SystemDetails
{
    private const string SystemVersionPlist = "/System/Library/CoreServices/SystemVersion.plist";

    /// <summary>e.g. ("macOS", "15.1", "24B83"); empty strings when unknown.</summary>
    public static (string Name, string Version, string Build) MacOSVersion()
    {
        if (!OperatingSystem.IsMacOS())
            return ("", "", "");
        try
        {
            var values = Plist.ReadTopLevelStrings(SystemVersionPlist);
            return (values.GetValueOrDefault("ProductName", "macOS"),
                values.GetValueOrDefault("ProductVersion", ""),
                values.GetValueOrDefault("ProductBuildVersion", ""));
        }
        catch (Exception)
        {
            return ("macOS", Environment.OSVersion.Version.ToString(), "");
        }
    }

    /// <summary>"macOS 15.1 Sequoia (24B83)", or the .NET OS description elsewhere.</summary>
    public static string MacOSDisplayName()
    {
        var (name, version, build) = MacOSVersion();
        if (version.Length == 0)
            return RuntimeInformation.OSDescription;
        var marketing = MacOSMarketingName(version);
        return $"{name} {version}{(marketing.Length > 0 ? " " + marketing : "")}{(build.Length > 0 ? $" ({build})" : "")}";
    }

    /// <summary>The marketing name for a macOS major version ("Sequoia"), or "".</summary>
    public static string MacOSMarketingName(string version) => version.Split('.')[0] switch
    {
        "11" => "Big Sur",
        "12" => "Monterey",
        "13" => "Ventura",
        "14" => "Sonoma",
        "15" => "Sequoia",
        "26" => "Tahoe",
        _ => "",
    };

    /// <summary>The model identifier, e.g. "Mac15,3"; "" when unknown.</summary>
    public static string ModelIdentifier() => SysctlString("hw.model") ?? "";

    /// <summary>CPU name, e.g. "Apple M3 Pro" or "Intel(R) Core(TM) i7-..."; "Unknown" when unavailable.</summary>
    public static string CpuName()
    {
        var name = SysctlString("machdep.cpu.brand_string");
        return string.IsNullOrWhiteSpace(name) ? "Unknown" : name.Trim();
    }

    /// <summary>Performance and efficiency core counts on Apple silicon; (0, 0) elsewhere.</summary>
    public static (int Performance, int Efficiency) CoreCounts() =>
        ((int)(SysctlInt64("hw.perflevel0.physicalcpu") ?? 0), (int)(SysctlInt64("hw.perflevel1.physicalcpu") ?? 0));

    /// <summary>True when this process runs translated by Rosetta 2 (an Intel build on Apple silicon).</summary>
    public static bool IsTranslated() => SysctlInt64("sysctl.proc_translated") == 1;

    /// <summary>
    /// Physical memory and the memory available to apps, in bytes; (0, 0) when the query fails.
    /// "Available" counts free, inactive, speculative and purgeable pages: macOS hands those out
    /// without swapping, so it matches what Activity Monitor suggests is left.
    /// </summary>
    public static (ulong Total, ulong Available) Memory()
    {
        if (!OperatingSystem.IsMacOS())
            return (0, 0);
        var total = (ulong)(SysctlInt64("hw.memsize") ?? 0);
        if (total == 0)
            return (0, 0);
        var vmStat = Processes.TryRun("/usr/bin/vm_stat", []);
        return vmStat is null ? (total, 0) : (total, Math.Min(total, ParseAvailableMemory(vmStat)));
    }

    /// <summary>Parses vm_stat output into available bytes; public for tests.</summary>
    public static ulong ParseAvailableMemory(string vmStat)
    {
        var pageSize = 4096UL;
        var header = PageSizeRegex().Match(vmStat);
        if (header.Success)
            pageSize = ulong.Parse(header.Groups[1].Value);
        ulong pages = 0;
        foreach (var name in (string[])["Pages free", "Pages inactive", "Pages speculative", "Pages purgeable"])
        {
            var match = Regex.Match(vmStat, "^" + Regex.Escape(name) + @":\s+(\d+)\.", RegexOptions.Multiline);
            if (match.Success)
                pages += ulong.Parse(match.Groups[1].Value);
        }
        return pages * pageSize;
    }

    /// <summary>Time since the Mac started (kern.boottime, so time asleep counts, as in "uptime").</summary>
    public static TimeSpan Uptime()
    {
        var boot = BootTime();
        return boot is { } time ? DateTimeOffset.UtcNow - time : TimeSpan.FromMilliseconds(Environment.TickCount64);
    }

    /// <summary>When the Mac started, or null when unknown.</summary>
    public static DateTimeOffset? BootTime()
    {
        if (!OperatingSystem.IsMacOS())
            return null;
        // struct timeval { long tv_sec; int tv_usec; } is 16 bytes on 64-bit macOS.
        var buffer = new byte[16];
        var length = (nint)buffer.Length;
        if (sysctlbyname("kern.boottime", buffer, ref length, IntPtr.Zero, 0) != 0 || length < 8)
            return null;
        var seconds = BitConverter.ToInt64(buffer, 0);
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    /// <summary>Main display size in pixels (CoreGraphics); (0, 0) when unknown.</summary>
    public static (int Width, int Height) ScreenSize()
    {
        if (!OperatingSystem.IsMacOS())
            return (0, 0);
        try
        {
            var display = CGMainDisplayID();
            var mode = CGDisplayCopyDisplayMode(display);
            if (mode == IntPtr.Zero)
                return ((int)CGDisplayPixelsWide(display), (int)CGDisplayPixelsHigh(display));
            try
            {
                // The mode's pixel size is the panel's real resolution on Retina displays.
                return ((int)CGDisplayModeGetPixelWidth(mode), (int)CGDisplayModeGetPixelHeight(mode));
            }
            finally
            {
                CFRelease(mode);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (0, 0);
        }
    }

    /// <summary>A sysctl string value (hw.model, machdep.cpu.brand_string...), or null.</summary>
    public static string? SysctlString(string name)
    {
        if (!OperatingSystem.IsMacOS())
            return null;
        nint length = 0;
        if (sysctlbyname(name, null, ref length, IntPtr.Zero, 0) != 0 || length <= 0)
            return null;
        var buffer = new byte[length];
        if (sysctlbyname(name, buffer, ref length, IntPtr.Zero, 0) != 0)
            return null;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, (int)length).TrimEnd('\0');
    }

    /// <summary>A sysctl integer value (32 or 64 bit), or null.</summary>
    public static long? SysctlInt64(string name)
    {
        if (!OperatingSystem.IsMacOS())
            return null;
        var buffer = new byte[8];
        var length = (nint)buffer.Length;
        if (sysctlbyname(name, buffer, ref length, IntPtr.Zero, 0) != 0)
            return null;
        return length switch
        {
            4 => BitConverter.ToInt32(buffer, 0),
            8 => BitConverter.ToInt64(buffer, 0),
            _ => null,
        };
    }

    [GeneratedRegex(@"page size of (\d+) bytes")]
    private static partial Regex PageSizeRegex();

    [DllImport("libc", SetLastError = true)]
    private static extern int sysctlbyname(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[]? oldp, ref nint oldlenp, IntPtr newp, nint newlen);

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreGraphics)]
    private static extern uint CGMainDisplayID();

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayPixelsWide(uint display);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayPixelsHigh(uint display);

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGDisplayCopyDisplayMode(uint display);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayModeGetPixelWidth(IntPtr mode);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayModeGetPixelHeight(IntPtr mode);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}
