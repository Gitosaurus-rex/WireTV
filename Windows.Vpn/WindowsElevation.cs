using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace OpenTv.Windows.Vpn;

/// <summary>
/// UAC helpers.
///
/// OpenTv itself runs as a normal user - forcing the whole app to run elevated
/// just to watch TV is bad practice. Only the tunnel commands need admin rights,
/// so they are launched through ShellExecute with the "runas" verb, producing one
/// UAC prompt per tunnel operation instead of one for the entire application.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsElevation
{
    /// <summary>Win32 error returned when the user dismisses the UAC prompt.</summary>
    public const int ErrorCancelled = 1223;

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// Runs a console command that requires administrator rights.
    ///
    /// When already elevated the process is started directly so stdout/stderr can be
    /// captured for error reporting. Otherwise ShellExecute + "runas" triggers UAC,
    /// which rules out output redirection - callers must then verify the result by
    /// observing its side effect (for tunnels: the service state).
    /// </summary>
    public static async Task<ElevatedResult> RunAsync(
        string fileName,
        string arguments,
        CancellationToken ct = default)
    {
        var elevated = IsElevated;

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = !elevated
        };

        if (elevated)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
        }
        else
        {
            startInfo.Verb = "runas";
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
                return new ElevatedResult(-1, string.Empty, "The process could not be started.", false);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new ElevatedResult(ErrorCancelled, string.Empty,
                "The administrator prompt was dismissed.", WasCancelledByUser: true);
        }

        string output = string.Empty;
        string error = string.Empty;

        if (elevated)
        {
            // Read both pipes before waiting, or a chatty child can fill a pipe buffer and deadlock.
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            output = await outputTask.ConfigureAwait(false);
            error = await errorTask.ConfigureAwait(false);
        }
        else
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        return new ElevatedResult(process.ExitCode, output, error, false);
    }
}

/// <param name="Output">Empty unless the command ran already-elevated.</param>
/// <param name="Error">Empty unless the command ran already-elevated.</param>
public readonly record struct ElevatedResult(
    int ExitCode,
    string Output,
    string Error,
    bool WasCancelledByUser)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>Best available description of a failure, for showing to the user.</summary>
    public string Describe()
    {
        if (WasCancelledByUser)
            return "The administrator prompt was dismissed.";

        var detail = !string.IsNullOrWhiteSpace(Error) ? Error.Trim()
            : !string.IsNullOrWhiteSpace(Output) ? Output.Trim()
            : null;

        return detail is null
            ? $"The command failed with exit code {ExitCode}."
            : $"The command failed with exit code {ExitCode}: {detail}";
    }
}
