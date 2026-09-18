using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace InterviewFlow.Core.Providers;

/// <summary>
/// One non-interactive CLI invocation: the executable, its arguments, what to
/// feed on stdin (the prompt — never on the command line, which Windows caps
/// at 32 K characters), where to run it, and environment overrides (null =
/// remove the variable from the child).
/// </summary>
public sealed record CliCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string StdIn,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment);

/// <summary>The process could not start, or exited non-zero.</summary>
public sealed class CliProcessException(string message, int exitCode, string stdErr) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
    public string StdErr { get; } = stdErr;
}

/// <summary>
/// Runs a <see cref="CliCommand"/> and streams its stdout line by line. The
/// seam the CLI providers are tested through: a fake yields canned JSONL
/// without spawning anything.
/// </summary>
public interface ICliRunner
{
    /// <summary>
    /// Stdout lines as they arrive. Once the process has exited, a non-zero
    /// exit code surfaces as <see cref="CliProcessException"/> carrying the
    /// captured stderr.
    /// </summary>
    IAsyncEnumerable<string> RunAsync(CliCommand command, CancellationToken ct = default);
}

/// <summary>
/// The real thing: <see cref="Process"/> with all three streams redirected,
/// UTF-8 both ways, stdin written and closed up front, stderr drained on the
/// side, and the whole process tree killed on cancellation.
/// </summary>
public sealed class ProcessCliRunner : ICliRunner
{
    public static readonly ProcessCliRunner Default = new();

    public async IAsyncEnumerable<string> RunAsync(
        CliCommand command, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = command.WorkingDirectory,
        };

        // npm-style .cmd shims only run under the command interpreter.
        var ext = Path.GetExtension(command.FileName);
        if (OperatingSystem.IsWindows() && ext is ".cmd" or ".bat")
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command.FileName);
        }
        else
        {
            psi.FileName = command.FileName;
        }

        foreach (var arg in command.Arguments)
            psi.ArgumentList.Add(arg);
        foreach (var (key, value) in command.Environment)
        {
            if (value is null)
                psi.Environment.Remove(key);
            else
                psi.Environment[key] = value;
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new CliProcessException($"Could not start {command.FileName}: {ex.Message}", -1, "");
        }

        using var killOnCancel = ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }
        });

        var stdErr = new StringBuilder();
        var drainErr = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync(ct) is { } line)
                    stdErr.AppendLine(line);
            }
            catch
            {
                // Cancelled or the pipe closed — whatever was captured is enough.
            }
        }, CancellationToken.None);

        var feedIn = Task.Run(async () =>
        {
            try
            {
                await process.StandardInput.WriteAsync(command.StdIn);
                await process.StandardInput.FlushAsync(ct);
            }
            catch (IOException)
            {
                // The child exited before reading everything; its exit code tells the story.
            }
            finally
            {
                process.StandardInput.Close();
            }
        }, CancellationToken.None);

        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            yield return line;

        await process.WaitForExitAsync(ct);
        await feedIn;
        await drainErr;

        if (process.ExitCode != 0)
        {
            throw new CliProcessException(
                $"{Path.GetFileName(command.FileName)} exited with code {process.ExitCode}",
                process.ExitCode, stdErr.ToString().Trim());
        }
    }
}
