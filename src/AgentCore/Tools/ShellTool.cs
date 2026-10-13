// run_command: a real shell inside the container. The container is the
// sandbox, so this is deliberately unrestricted - the agent can install
// packages, run builds, use git and so on. Only /agent/workspace and
// /agent/memory are mounted from the host, so that's all it can reach.

using System.Diagnostics;
using System.Text;

namespace AgentCore;

public static class ShellTool
{
    public static Tool Create(Sandbox sb, int defaultTimeoutSeconds)
    {
        return new Tool("run_command",
            "Run a shell command (bash) inside the Linux container and return its output and exit code. " +
            "Use it to run code, tests and builds, use git, install packages (pip, npm, dotnet, sudo apt-get) and inspect the system. " +
            "Runs in the workspace unless workdir is set. Commands can't ask questions: pass -y style flags. " +
            "Long-running servers must be started in the background (e.g. 'nohup cmd > log.txt 2>&1 &').",
            Schema.Obj(("command", "string", "The command line.", true),
                       ("workdir", "string", "Folder to run in, relative to the workspace. Default '.'", false),
                       ("timeout_seconds", "integer", $"Kill the command after this long. Default {defaultTimeoutSeconds}, max 900.", false)),
            (a, ct) => RunAsync(sb, Args.Req(a, "command"), Args.Str(a, "workdir"),
                                Math.Clamp(Args.Int(a, "timeout_seconds") ?? defaultTimeoutSeconds, 1, 900), ct));
    }

    private static async Task<string> RunAsync(Sandbox sb, string command, string? workdir, int timeoutSeconds, CancellationToken ct)
    {
        var dir = sb.Resolve(workdir);
        if (!Directory.Exists(dir)) return $"Error: workdir '{workdir}' does not exist.";

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
            WorkingDirectory = dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        psi.ArgumentList.Add(command);
        psi.Environment["TERM"] = "dumb";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var gate = new object();
        void Collect(object _, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lock (gate) output.AppendLine(e.Data);
        }
        process.OutputDataReceived += Collect;
        process.ErrorDataReceived += Collect;

        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"Error: couldn't start the shell: {ex.Message}";
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close(); // nothing will ever answer a prompt

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            process.WaitForExit(); // flush the async output readers
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            timedOut = true;
        }

        string text;
        lock (gate) text = output.ToString().TrimEnd();
        text = Text.ClipMiddle(text, 6000, 12000);

        var header = timedOut ? $"Timed out after {timeoutSeconds}s and was killed." : $"Exit code {process.ExitCode}.";
        return text.Length == 0 ? header + " (no output)" : header + "\n" + text;
    }
}
