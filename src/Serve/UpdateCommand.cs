using System.Diagnostics;

namespace KY.AI.Serve;

// `<tool> update` — update THIS tool to the latest version using whichever package manager it was
// installed with. The install route is detected from the running executable's path:
//   * under a node_modules path  -> npm     (npm install --global <pkg>@latest)
//   * otherwise (a .NET global tool) -> dotnet (dotnet tool update --global <pkg>)
//
// A running process can't overwrite its own files on Windows, so a direct self-update would fail
// with a file lock; there the updater is launched in a new window that waits for this process to
// exit first. On POSIX the running binary can be replaced in place, so it runs inline.
public static class UpdateCommand
{
    public static int Run(string toolName, string dotnetPackageId, string? npmPackageId, string[] rest)
    {
        Cli.TrySetUtf8Console();

        var self = (Environment.ProcessPath ?? "").Replace('\\', '/');
        var viaNpm = npmPackageId is not null &&
                     self.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase);

        var command = viaNpm
            ? $"npm install --global {npmPackageId}@latest"
            : $"dotnet tool update --global {dotnetPackageId}";

        Console.WriteLine($"{toolName}: updating via  {command}");

        // Windows can't replace the running exe/DLLs in place — run the updater after we exit.
        if (OperatingSystem.IsWindows())
            return LaunchDetachedWindows(toolName, command);

        // POSIX: replacing a running binary's file is fine — run inline and show output.
        return RunInline(viaNpm ? "npm" : "dotnet",
            viaNpm ? ["install", "--global", $"{npmPackageId}@latest"]
                   : ["tool", "update", "--global", dotnetPackageId]);
    }

    private static int RunInline(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) { Console.Error.WriteLine($"could not start {exe}"); return 1; }
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"'{exe}' not found on PATH — is it installed?");
            return 1;
        }
    }

    // Open a new console that waits ~2s (so this process releases its files), runs the update, then
    // pauses so the result stays visible after this process has exited.
    private static int LaunchDetachedWindows(string toolName, string command)
    {
        try
        {
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            var shell = string.IsNullOrEmpty(comspec) ? "cmd.exe" : comspec;
            var psi = new ProcessStartInfo(shell)
            {
                UseShellExecute = true,   // its own window
                Arguments = $"/c timeout /t 2 /nobreak >nul & {command} & echo. & pause",
            };
            Process.Start(psi);
            Console.WriteLine($"{toolName}: the update opens in a new window and runs once this one exits.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{toolName}: could not launch the updater ({ex.Message}). Run it yourself:");
            Console.Error.WriteLine($"  {command}");
            return 1;
        }
    }
}
