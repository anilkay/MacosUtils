using System.Diagnostics;

namespace MacosUtils.Core;

/// <summary>Output of a finished command-line tool.</summary>
public sealed record ToolResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Runs the macOS command-line tools the app reads information from (sysctl, dscacheutil, lsof...).
/// Tools are always started by their full path: an app opened from Finder gets only the system
/// PATH, and a full path also means no planted tool earlier on PATH can run instead. Arguments
/// go through <see cref="ProcessStartInfo.ArgumentList"/>, never through a shell.
/// </summary>
public static class Processes
{
    /// <summary>Runs <paramref name="tool"/> and returns its output; kills it after <paramref name="timeout"/>.</summary>
    public static async Task<ToolResult> RunAsync(
        string tool,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = CreateStartInfo(tool, arguments) };
        process.Start();
        // Both pipes are drained concurrently so a full stderr buffer cannot block the tool.
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(tool)} did not finish in time.");
        }
        return new ToolResult(process.ExitCode, await output, await error);
    }

    /// <summary>Synchronous <see cref="RunAsync"/> for callers that are already on a background thread.</summary>
    public static ToolResult Run(string tool, IEnumerable<string> arguments, TimeSpan? timeout = null) =>
        RunAsync(tool, arguments, timeout).GetAwaiter().GetResult();

    /// <summary>Stdout of a successful run, or null when the tool is missing, fails or times out.</summary>
    public static string? TryRun(string tool, IEnumerable<string> arguments, TimeSpan? timeout = null)
    {
        if (!File.Exists(tool))
            return null;
        try
        {
            var result = Run(tool, arguments, timeout);
            return result.Success ? result.Output : null;
        }
        catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Starts a tool without waiting for it (open -R ...).</summary>
    public static void Start(string tool, IEnumerable<string> arguments)
    {
        var info = CreateStartInfo(tool, arguments);
        info.RedirectStandardOutput = false;
        info.RedirectStandardError = false;
        using var _ = Process.Start(info);
    }

    private static ProcessStartInfo CreateStartInfo(string tool, IEnumerable<string> arguments)
    {
        if (!Path.IsPathFullyQualified(tool))
            throw new ArgumentException("Tools are started by their full path.", nameof(tool));
        var info = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Tools print in English and in a fixed format whatever the user's language is.
            Environment = { ["LC_ALL"] = "en_US.UTF-8", ["LANG"] = "en_US.UTF-8" },
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }
}
