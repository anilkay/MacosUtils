namespace MacosUtils.Core.SystemInfo;

/// <summary>The state and health of a MacBook's battery.</summary>
/// <param name="ChargePercent">Current charge, 0-100.</param>
/// <param name="HealthPercent">Full-charge capacity against the design capacity, as System Settings › Battery shows it.</param>
/// <param name="Condition">Apple's verdict: "Normal" or "Service Recommended"; "" when unknown.</param>
/// <param name="DesignCycleCount">Cycles the battery is rated for (1000 for MacBooks since 2009), when the battery reports it.</param>
/// <param name="FullChargeMah">What the battery holds when full now.</param>
/// <param name="TemperatureCelsius">Battery temperature.</param>
/// <param name="AmperageMa">Negative while discharging.</param>
/// <param name="TimeRemaining">To empty while on battery, to full while charging; null while macOS is still estimating.</param>
/// <param name="Adapter">The connected power adapter, e.g. "96W USB-C Power Adapter"; "" when none.</param>
public sealed record BatteryStatus(
    int? ChargePercent,
    bool IsCharging,
    bool ExternalPower,
    bool FullyCharged,
    int? CycleCount,
    int? DesignCycleCount,
    double? HealthPercent,
    string Condition,
    int? DesignCapacityMah,
    int? FullChargeMah,
    int? CurrentChargeMah,
    double? TemperatureCelsius,
    int? VoltageMv,
    int? AmperageMa,
    TimeSpan? TimeRemaining,
    string Adapter,
    string DeviceName,
    bool PermanentFailure)
{
    /// <summary>Power flowing in (charging, positive) or out (on battery, negative), in watts.</summary>
    public double? PowerWatts => VoltageMv is { } v && AmperageMa is { } a ? v * (double)a / 1_000_000 : null;

    /// <summary>Health below 80% or a condition other than Normal: Apple's threshold for a service.</summary>
    public bool NeedsAttention =>
        PermanentFailure
        || (Condition.Length > 0 && !Condition.Equals("Normal", StringComparison.OrdinalIgnoreCase))
        || HealthPercent < 80;

    /// <summary>"Charging", "On battery", "Fully charged"...</summary>
    public string State => (IsCharging, ExternalPower, FullyCharged) switch
    {
        (true, _, _) => "Charging",
        (_, true, true) => "Fully charged, on power adapter",
        // macOS holds the charge at 80% (Optimized Battery Charging) or a set limit while plugged in.
        (_, true, false) => "On power adapter, not charging",
        _ => "On battery",
    };
}

/// <summary>
/// Battery health from the AppleSmartBattery registry entry (ioreg; cycle count, capacities,
/// temperature, current, adapter) and Apple's own "Condition" and "Maximum Capacity" from
/// system_profiler. A Mac without a battery (desktops, virtual machines) gives null. Read-only.
/// </summary>
public static class Battery
{
    private const string Ioreg = "/usr/sbin/ioreg";
    private const string SystemProfiler = "/usr/sbin/system_profiler";

    // AvgTimeToEmpty and friends hold this while the estimate is not ready.
    private const long UnknownTime = 65535;

    public static BatteryStatus? Get()
    {
        if (!OperatingSystem.IsMacOS())
            return null;
        var ioreg = Processes.TryRun(Ioreg, ["-r", "-c", "AppleSmartBattery", "-a"]);
        if (string.IsNullOrWhiteSpace(ioreg))
            return null;
        Dictionary<string, object?>? entry;
        try
        {
            entry = (Plist.Parse(ioreg) as List<object?>)?.OfType<Dictionary<string, object?>>().FirstOrDefault();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
        if (entry is null)
            return null;
        var profiler = Processes.TryRun(SystemProfiler, ["SPPowerDataType"], TimeSpan.FromSeconds(20));
        return FromRegistry(entry, profiler);
    }

    /// <summary>
    /// Builds the status from the AppleSmartBattery properties and, when given, the text of
    /// "system_profiler SPPowerDataType". On Apple silicon CurrentCapacity and MaxCapacity are
    /// percentages and the mAh values are the AppleRaw* ones; on Intel they are mAh. Public for tests.
    /// </summary>
    public static BatteryStatus? FromRegistry(Dictionary<string, object?> entry, string? systemProfilerText = null)
    {
        if (entry.GetValueOrDefault("BatteryInstalled") is false)
            return null;

        var design = Int(entry, "DesignCapacity");
        var max = Int(entry, "MaxCapacity");
        var current = Int(entry, "CurrentCapacity");
        var percentScale = max is <= 100;
        var fullMah = Int(entry, "AppleRawMaxCapacity") ?? (percentScale ? Int(entry, "NominalChargeCapacity") : max);
        var currentMah = Int(entry, "AppleRawCurrentCapacity") ?? (percentScale ? null : current);
        int? charge = percentScale ? current
            : current is { } c && max is > 0 ? (int)Math.Round(c * 100.0 / max.Value) : null;

        var profiler = ParseSystemProfiler(systemProfilerText ?? "");
        double? health = profiler.MaximumCapacity;
        // Apple compares the nominal full charge with the design capacity; AppleRawMaxCapacity runs a little lower.
        var healthBase = Int(entry, "NominalChargeCapacity") ?? fullMah;
        if (health is null && healthBase is > 0 && design is > 0)
            health = Math.Round(healthBase.Value * 100.0 / design.Value, 1);

        var charging = entry.GetValueOrDefault("IsCharging") is true;
        var external = entry.GetValueOrDefault("ExternalConnected") is true;
        var time = charging ? Minutes(entry, "AvgTimeToFull")
            : external ? null
            : Minutes(entry, "AvgTimeToEmpty") ?? Minutes(entry, "TimeRemaining");

        var temperature = Int(entry, "Temperature") is { } t && t > 0 ? t / 100.0 : (double?)null;
        var designCycles = Int(entry, "DesignCycleCount9C")
            ?? (entry.GetValueOrDefault("BatteryData") is Dictionary<string, object?> data
                ? data.Where(kv => kv.Key.StartsWith("DesignCycleCount", StringComparison.Ordinal)).Select(kv => kv.Value).OfType<long>()
                    .Where(v => v > 0).Select(v => (int?)v).FirstOrDefault()
                : null);

        return new BatteryStatus(
            charge is { } p ? Math.Clamp(p, 0, 100) : null,
            charging,
            external,
            entry.GetValueOrDefault("FullyCharged") is true,
            Int(entry, "CycleCount") ?? profiler.CycleCount,
            designCycles,
            health,
            profiler.Condition,
            design,
            fullMah,
            currentMah,
            temperature,
            Int(entry, "Voltage"),
            Int(entry, "Amperage") ?? Int(entry, "InstantAmperage"),
            time,
            external ? AdapterName(entry) : "",
            entry.GetValueOrDefault("DeviceName") as string ?? "",
            Int(entry, "PermanentFailureStatus") is > 0);
    }

    /// <summary>
    /// "Condition: Normal", "Maximum Capacity: 92%" and "Cycle Count: 120" from
    /// system_profiler SPPowerDataType (English: Processes sets the locale). Public for tests.
    /// </summary>
    public static (string Condition, double? MaximumCapacity, int? CycleCount) ParseSystemProfiler(string output)
    {
        string condition = "";
        double? capacity = null;
        int? cycles = null;
        foreach (var raw in output.Split('\n'))
        {
            var colon = raw.IndexOf(':');
            if (colon < 0)
                continue;
            var key = raw[..colon].Trim();
            var value = raw[(colon + 1)..].Trim();
            if (key == "Condition" && condition.Length == 0)
                condition = value;
            else if (key == "Maximum Capacity" && capacity is null
                     && double.TryParse(value.TrimEnd('%').Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c))
                capacity = c;
            else if (key == "Cycle Count" && cycles is null && int.TryParse(value, out var n))
                cycles = n;
        }
        return (condition, capacity, cycles);
    }

    /// <summary>The status as labelled values for display; <c>Warn</c> marks what needs a look (health, condition, failure).</summary>
    public static IEnumerable<(string Label, string Value, bool Warn)> Describe(BatteryStatus b)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (b.ChargePercent is { } charge)
            yield return ("Charge", $"{charge}%", false);
        yield return ("State", b.State, false);
        if (b.TimeRemaining is { } time)
            yield return (b.IsCharging ? "Time to Full" : "Time Remaining", $"{(int)time.TotalHours}:{time.Minutes:00}", false);
        if (b.Condition.Length > 0)
            yield return ("Condition", b.Condition, !b.Condition.Equals("Normal", StringComparison.OrdinalIgnoreCase));
        if (b.HealthPercent is { } health)
            yield return ("Maximum Capacity", $"{health.ToString("0.#", inv)}% of the design capacity" + (health < 80 ? " (below 80%: Apple recommends service)" : ""), health < 80);
        if (b.CycleCount is { } cycles)
            yield return ("Cycle Count", b.DesignCycleCount is { } rated ? $"{cycles} of {rated} rated cycles" : cycles.ToString(inv),
                b.DesignCycleCount is { } r && cycles >= r);
        if (b.FullChargeMah is { } full)
            yield return ("Full Charge Capacity", b.DesignCapacityMah is { } design ? $"{full} mAh (designed for {design} mAh)" : $"{full} mAh", false);
        if (b.CurrentChargeMah is { } now)
            yield return ("Current Charge", $"{now} mAh", false);
        if (b.TemperatureCelsius is { } temperature)
            yield return ("Temperature", $"{temperature.ToString("0.0", inv)} °C", temperature >= 45);
        if (b.PowerWatts is { } watts && Math.Abs(watts) >= 0.05)
            yield return (watts < 0 ? "Power Draw" : "Charging Power", $"{Math.Abs(watts).ToString("0.0", inv)} W", false);
        if (b.VoltageMv is { } mv)
            yield return ("Voltage", $"{(mv / 1000.0).ToString("0.00", inv)} V", false);
        if (b.Adapter.Length > 0)
            yield return ("Power Adapter", b.Adapter, false);
        if (b.DeviceName.Length > 0)
            yield return ("Battery Controller", b.DeviceName, false);
        if (b.PermanentFailure)
            yield return ("Permanent Failure", "The battery reports a permanent failure; have it serviced", true);
    }

    // Apple silicon: AppleRawAdapterDetails is a list; Intel: AdapterDetails is one dictionary.
    private static string AdapterName(Dictionary<string, object?> entry)
    {
        var details = entry.GetValueOrDefault("AdapterDetails") as Dictionary<string, object?>
            ?? (entry.GetValueOrDefault("AppleRawAdapterDetails") as List<object?>)?.OfType<Dictionary<string, object?>>().FirstOrDefault();
        if (details is null)
            return "Connected";
        var name = details.GetValueOrDefault("Name") as string;
        var watts = Int(details, "Watts");
        if (!string.IsNullOrWhiteSpace(name))
            return watts is > 0 && !name.Contains($"{watts}W", StringComparison.Ordinal) ? $"{name} ({watts} W)" : name;
        return watts is > 0 ? $"{watts} W adapter" : "Connected";
    }

    private static int? Int(Dictionary<string, object?> dict, string key) => dict.GetValueOrDefault(key) switch
    {
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        _ => null,
    };

    private static TimeSpan? Minutes(Dictionary<string, object?> dict, string key) =>
        dict.GetValueOrDefault(key) is long m && m is > 0 and < UnknownTime ? TimeSpan.FromMinutes(m) : null;
}
