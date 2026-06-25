using System.Diagnostics;
using System.IO;
using System.Text;
using Editor.Core.Formatting;

namespace Editor.Controls.Formatting;

/// <summary>
/// Runs a configured <see cref="FormatterDef"/> as a one-shot child process: the buffer text is written
/// to its stdin and the formatted document is read back from stdout (UTF-8, no BOM). A non-zero exit code
/// or a launch failure returns <see cref="RunResult.Error"/> and leaves the buffer untouched — a formatter
/// must never be able to corrupt the document.
/// </summary>
public static class FormatterRunner
{
    public readonly record struct RunResult(bool Ok, string? Output, string? Error);

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Run <paramref name="def"/> over <paramref name="input"/>, substituting <c>{file}</c> in its args with <paramref name="filePath"/>.</summary>
    public static RunResult Run(FormatterDef def, string? filePath, string input, int timeoutMs = 15000)
    {
        try
        {
            var psi = new ProcessStartInfo(def.Executable)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = Utf8NoBom,
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom,
            };
            if (!string.IsNullOrEmpty(filePath))
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
            }
            foreach (var arg in def.Args)
                psi.ArgumentList.Add(arg.Replace("{file}", filePath ?? ""));

            using var proc = Process.Start(psi);
            if (proc is null) return new RunResult(false, null, $"could not start {def.Executable}");

            // Read stdout/stderr on background tasks so a formatter that writes a lot can't deadlock on a full pipe.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            proc.StandardInput.Write(input);
            proc.StandardInput.Close();

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new RunResult(false, null, $"{def.Executable} timed out");
            }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            if (proc.ExitCode != 0)
            {
                var msg = string.IsNullOrWhiteSpace(stderr) ? $"exit {proc.ExitCode}" : stderr.Trim();
                return new RunResult(false, null, msg);
            }
            return new RunResult(true, stdout, null);
        }
        catch (Exception ex)
        {
            // Most commonly the executable isn't on PATH (Win32Exception) — surface a short reason.
            return new RunResult(false, null, ex.Message);
        }
    }

    /// <summary>True when <paramref name="executable"/> resolves on PATH (honouring PATHEXT on Windows).</summary>
    public static bool IsOnPath(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        if (File.Exists(executable)) return true;   // an absolute/relative path was given

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : [""];
        foreach (var dir in paths)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string candidate;
            try { candidate = Path.Combine(dir, executable); }
            catch { continue; }
            if (File.Exists(candidate)) return true;
            foreach (var ext in exts)
                if (ext.Length > 0 && File.Exists(candidate + ext)) return true;
        }
        return false;
    }
}
