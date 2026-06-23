// pack.cs — pack the KY.AI suite for distribution.
//
//   dotnet run scripts/pack.cs        (or:  scripts\pack.cmd)
//     --skip-npm     only build the NuGet packages
//     --skip-nuget   only build the npm packages
//
// Produces two distribution channels into artifacts\:
//   * NuGet  (artifacts\*.nupkg)      — Serve (lib) + Ng/Net as portable .NET global tools,
//                                        for users who have the .NET SDK (e.g. full-stack devs).
//   * npm    (artifacts\npm\...)       — @ky-ai/ng plus per-platform packages bundling a
//                                        self-contained ky-ai-ng binary, so Angular devs on
//                                        macOS/Linux/Windows need no .NET runtime at all.
//
// Pack/publish compile in Release themselves, so there's no separate build step. Push both
// channels with scripts\publish.cmd; build a runnable local copy with scripts\dist.cmd.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

var root = RepoRoot();
var artifacts = Path.Combine(root, "artifacts");

bool skipNuget = args.Contains("--skip-nuget");
bool skipNpm = args.Contains("--skip-npm");
if (skipNuget && skipNpm)
{
    Console.Error.WriteLine("nothing to do: --skip-nuget and --skip-npm together.");
    return 2;
}

if (!skipNuget) { var rc = PackNuget(root, artifacts); if (rc != 0) return rc; }
if (!skipNpm) { var rc = PackNpm(root, artifacts); if (rc != 0) return rc; }

Console.WriteLine("Pack complete.");
return 0;

// ---- NuGet: Serve library + Ng/Net portable global tools --------------------

static int PackNuget(string root, string artifacts)
{
    foreach (var f in Directory.Exists(artifacts) ? Directory.GetFiles(artifacts, "*.nupkg") : [])
        File.Delete(f);
    foreach (var f in Directory.Exists(artifacts) ? Directory.GetFiles(artifacts, "*.snupkg") : [])
        File.Delete(f);

    string[] projects =
    [
        Path.Combine(root, "src", "Serve", "KY.AI.Serve.csproj"),
        Path.Combine(root, "src", "Ng", "KY.AI.Ng.csproj"),
        Path.Combine(root, "src", "Net", "KY.AI.Net.csproj"),
    ];

    foreach (var proj in projects)
    {
        Console.WriteLine($"==> dotnet pack {Path.GetFileName(proj)} -c Release -o artifacts");
        int code = Run(root, "dotnet", "pack", proj, "-c", "Release", "-o", artifacts, "--nologo");
        if (code != 0) { Console.Error.WriteLine($"pack FAILED for {Path.GetFileName(proj)} (exit {code})"); return code; }
    }

    var packages = Directory.GetFiles(artifacts, "*.nupkg").OrderBy(f => f).ToArray();
    // dotnet pack only warns (exit 0) when packaging is disabled, so confirm each one landed.
    string[] expected = ["KY.AI.Serve", "KY.AI.Ng", "KY.AI.Net"];
    var missing = expected
        .Where(id => !packages.Any(p => Path.GetFileName(p).StartsWith(id + ".", StringComparison.OrdinalIgnoreCase)))
        .ToArray();
    if (missing.Length > 0)
    {
        Console.Error.WriteLine($"no package produced for: {string.Join(", ", missing)} " +
                                "(is <IsPackable> set? the Web SDK disables it by default)");
        return 1;
    }

    Console.WriteLine($"NuGet: packed {packages.Length} package(s) into {artifacts}");
    foreach (var p in packages) Console.WriteLine($"  + {Path.GetFileName(p)}");
    return 0;
}

// ---- npm: @ky-ai/ng + per-platform self-contained binaries ------------------

static int PackNpm(string root, string artifacts)
{
    var ngCsproj = Path.Combine(root, "src", "Ng", "KY.AI.Ng.csproj");
    var version = ReadVersion(ngCsproj);

    var npmOut = Path.Combine(artifacts, "npm");
    if (Directory.Exists(npmOut)) Directory.Delete(npmOut, recursive: true);
    Directory.CreateDirectory(npmOut);

    // npm platform/arch  ->  .NET RID. process.platform / process.arch on the consuming machine
    // pick which optional dependency npm installs.
    (string suffix, string rid, string os, string cpu)[] targets =
    [
        ("darwin-arm64", "osx-arm64", "darwin", "arm64"),
        ("darwin-x64", "osx-x64", "darwin", "x64"),
        ("linux-x64", "linux-x64", "linux", "x64"),
        ("win32-x64", "win-x64", "win32", "x64"),
    ];

    var optionalDeps = new SortedDictionary<string, string>(StringComparer.Ordinal);

    foreach (var t in targets)
    {
        var pkgName = $"@ky-ai/ng-{t.suffix}";
        var pkgDir = Path.Combine(npmOut, $"ng-{t.suffix}");
        var binDir = Path.Combine(pkgDir, "bin");
        var binName = t.rid.StartsWith("win") ? "ky-ai-ng.exe" : "ky-ai-ng";

        Console.WriteLine($"==> dotnet publish ky-ai-ng -r {t.rid} --self-contained (single file)");
        int code = Run(root, "dotnet", "publish", ngCsproj, "-c", "Release",
            "-r", t.rid, "--self-contained",
            "-p:PublishSingleFile=true", "-p:DebugType=none", "-p:DebugSymbols=false",
            "-o", binDir, "--nologo");
        if (code != 0) { Console.Error.WriteLine($"npm build FAILED for {t.rid} (exit {code})"); return code; }

        // Single-file publish bundles everything into the one binary; drop the Web-SDK
        // leftovers (a tiny static-assets manifest, any stray pdb) so the package is just the exe.
        foreach (var f in Directory.GetFiles(binDir))
        {
            var n = Path.GetFileName(f);
            if (n.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || n.Contains("staticwebassets"))
                File.Delete(f);
        }
        if (!File.Exists(Path.Combine(binDir, binName)))
        { Console.Error.WriteLine($"expected binary '{binName}' not produced for {t.rid}"); return 1; }

        WritePlatformJson(Path.Combine(pkgDir, "package.json"), pkgName, version,
            $"ky-ai-ng prebuilt binary for {t.suffix} (self-contained; no .NET runtime needed).",
            t.os, t.cpu);

        optionalDeps[pkgName] = version;
        Console.WriteLine($"  + {pkgName}  ({binName})");
    }

    // Main package @ky-ai/ng: the Node launcher + readme + the per-platform optional deps.
    var mainDir = Path.Combine(npmOut, "ng");
    Directory.CreateDirectory(Path.Combine(mainDir, "bin"));
    File.Copy(Path.Combine(root, "npm", "ky-ai-ng", "bin", "ky-ai-ng.js"),
              Path.Combine(mainDir, "bin", "ky-ai-ng.js"), overwrite: true);
    var ngReadme = Path.Combine(root, "src", "Ng", "README.md");
    if (File.Exists(ngReadme)) File.Copy(ngReadme, Path.Combine(mainDir, "README.md"), overwrite: true);

    WriteMainJson(Path.Combine(mainDir, "package.json"), version, optionalDeps);

    Console.WriteLine($"  + @ky-ai/ng  (main; version {version})");
    Console.WriteLine($"npm: packed {targets.Length + 1} package(s) into {npmOut}");
    return 0;
}

// ---- helpers ----------------------------------------------------------------

static string ReadVersion(string csproj)
{
    var m = Regex.Match(File.ReadAllText(csproj), @"<Version>\s*([^<\s]+)\s*</Version>");
    if (!m.Success) throw new InvalidOperationException($"no <Version> element in {csproj}");
    return m.Groups[1].Value;
}

// Hand-written via Utf8JsonWriter: file-based apps disable reflection-based JsonSerializer,
// and the DOM writer needs no reflection (and gets the escaping right).
static JsonWriterOptions JsonOpts() => new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

static void WritePlatformJson(string path, string name, string version, string description, string os, string cpu)
{
    using var stream = File.Create(path);
    using var w = new Utf8JsonWriter(stream, JsonOpts());
    w.WriteStartObject();
    w.WriteString("name", name);
    w.WriteString("version", version);
    w.WriteString("description", description);
    w.WriteString("license", "MIT");
    w.WriteStartArray("os"); w.WriteStringValue(os); w.WriteEndArray();
    w.WriteStartArray("cpu"); w.WriteStringValue(cpu); w.WriteEndArray();
    w.WriteStartArray("files"); w.WriteStringValue("bin"); w.WriteEndArray();
    w.WriteEndObject();
}

static void WriteMainJson(string path, string version, SortedDictionary<string, string> optionalDeps)
{
    using var stream = File.Create(path);
    using var w = new Utf8JsonWriter(stream, JsonOpts());
    w.WriteStartObject();
    w.WriteString("name", "@ky-ai/ng");
    w.WriteString("version", version);
    w.WriteString("description", "Run Angular dev servers with build state and logs exposed to AI agents over MCP. Self-contained; no .NET runtime required.");
    w.WriteString("license", "MIT");
    w.WriteStartObject("bin"); w.WriteString("ky-ai-ng", "bin/ky-ai-ng.js"); w.WriteEndObject();
    w.WriteStartArray("files"); w.WriteStringValue("bin"); w.WriteStringValue("README.md"); w.WriteEndArray();
    w.WriteStartObject("optionalDependencies");
    foreach (var kv in optionalDeps) w.WriteString(kv.Key, kv.Value);
    w.WriteEndObject();
    w.WriteStartArray("keywords");
    foreach (var k in new[] { "angular", "mcp", "ai", "dev-server", "cli" }) w.WriteStringValue(k);
    w.WriteEndArray();
    w.WriteEndObject();
}

static int Run(string cwd, string exe, params string[] args)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = cwd };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
    p.WaitForExit();
    return p.ExitCode;
}

static string RepoRoot([CallerFilePath] string scriptPath = "")
{
    var dir = Path.GetDirectoryName(scriptPath)!;
    var root = Path.GetFullPath(Path.Combine(dir, ".."));
    if (!Directory.Exists(Path.Combine(root, "src")))
        throw new DirectoryNotFoundException($"repo root not found above '{scriptPath}' (no src\\ folder)");
    return root;
}
