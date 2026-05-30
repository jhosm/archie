using System.Diagnostics;

namespace Babelstone.Packs;

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Runs an out-of-process tool (oras/cosign) at LOAD time and captures its result. The
/// process's OWN exit code is the success signal — callers treat a non-zero code as fatal
/// (fail-loud), never inferring success from non-empty stdout. This is library code invoked
/// only by the loader; it is never reachable from an <c>IEventHandler.Apply</c> body (the
/// BENG002 analyser bans <c>Process.Start</c> there, not here).
/// </summary>
internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new PackLoadException(null, null,
                $"could not start '{fileName}' (is it on PATH? the engine toolchain pins it): {ex.Message}");
        }

        var stdOut = process.StandardOutput.ReadToEndAsync(ct);
        var stdErr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new ProcessResult(process.ExitCode, await stdOut, await stdErr);
    }

    /// <summary>Last <paramref name="max"/> chars of tool output, for a diagnosable (but bounded) error message.</summary>
    public static string Tail(string text, int max = 600)
    {
        text = text.Trim();
        return text.Length <= max ? text : "…" + text[^max..];
    }
}
