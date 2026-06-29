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
            var args = new string[def.Args.Length];
            for (var i = 0; i < def.Args.Length; i++)
                args[i] = def.Args[i].Replace("{file}", filePath ?? "");

            var psi = BuildStartInfo(def.Executable, args);
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.StandardInputEncoding = Utf8NoBom;
            psi.StandardOutputEncoding = Utf8NoBom;
            psi.StandardErrorEncoding = Utf8NoBom;
            if (!string.IsNullOrEmpty(filePath))
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
            }

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

    /// <summary>
    /// Build the <see cref="ProcessStartInfo"/> for a formatter. On Windows, npm/yarn-style global tools are
    /// installed as <c>.cmd</c>/<c>.bat</c> shims (e.g. <c>prettier.cmd</c>), which <see cref="Process"/> cannot
    /// launch directly with <c>UseShellExecute=false</c> — it can only start real <c>.exe</c>/<c>.com</c> images,
    /// so a bare <c>prettier</c> fails with "cannot find the file specified". For those we go through
    /// <c>cmd.exe /c</c> (which resolves the shim via PATHEXT and still supports stdin→stdout redirection).
    /// Real executables are launched directly via <see cref="ProcessStartInfo.ArgumentList"/> (no quoting hazard).
    /// </summary>
    private static ProcessStartInfo BuildStartInfo(string executable, string[] args)
    {
        var resolved = ResolveOnPath(executable);
        if (OperatingSystem.IsWindows() && IsShim(resolved ?? executable))
        {
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(comspec)) comspec = "cmd.exe";
            // cmd strips the outermost pair of quotes from the string after /c, so wrap the whole
            // "program args…" in one extra pair to keep each token's own quotes intact (the classic
            // `cmd /c " "prog" "arg" "` trick). Use the raw Arguments string, not ArgumentList, to control it.
            var target = resolved ?? executable;
            var sb = new StringBuilder("/c \"");
            sb.Append(QuoteForCmd(target));
            foreach (var a in args)
            {
                sb.Append(' ');
                sb.Append(QuoteForCmd(a));
            }
            sb.Append('"');
            return new ProcessStartInfo(comspec) { Arguments = sb.ToString() };
        }

        var psi = new ProcessStartInfo(resolved ?? executable);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>True when the path looks like a Windows command shim that needs <c>cmd.exe</c> to launch.</summary>
    private static bool IsShim(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Quote a single token for a <c>cmd.exe</c> command line: wrap in quotes when it contains
    /// whitespace or quotes, doubling any trailing backslashes so they don't escape the closing quote.</summary>
    private static string QuoteForCmd(string token)
    {
        if (token.Length > 0 && token.IndexOfAny([' ', '\t', '"']) < 0)
            return token;

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in token)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
            sb.Append(c);
        }
        if (backslashes > 0) sb.Append('\\', backslashes * 2);   // before the closing quote: double them
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Resolve <paramref name="executable"/> to a full path via PATH (honouring PATHEXT on Windows),
    /// preferring real images (.exe/.com) then shims (.bat/.cmd) so a tool that ships both is launched as the exe.
    /// Returns null when nothing matches (the caller then tries the bare name and surfaces the OS error).</summary>
    private static string? ResolveOnPath(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        if (executable.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return File.Exists(executable) ? executable : null;   // an absolute/relative path was given

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        // Order matters: try a self-contained image before a shim of the same name.
        var exts = OperatingSystem.IsWindows()
            ? OrderPathExt(Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            : [""];

        foreach (var dir in paths)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string candidate;
            try { candidate = Path.Combine(dir, executable); }
            catch { continue; }
            if (Path.HasExtension(executable) && File.Exists(candidate)) return candidate;
            foreach (var ext in exts)
                if (ext.Length > 0 && File.Exists(candidate + ext)) return candidate + ext;
        }
        return null;
    }

    /// <summary>PATHEXT entries reordered so executables (.exe/.com) come before shims (.bat/.cmd).</summary>
    private static string[] OrderPathExt(string pathext)
    {
        var raw = pathext.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int Rank(string e) => e.ToUpperInvariant() switch
        {
            ".COM" => 0,
            ".EXE" => 1,
            ".BAT" => 2,
            ".CMD" => 3,
            _ => 4,
        };
        return raw.OrderBy(Rank).ToArray();
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
