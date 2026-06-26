namespace KY.AI.Serve;

// Tool-specific strategy for a supervisor: everything the shared spawn/tee/track/REST/
// registration machinery needs that differs between the Angular and .NET tools. Created once
// per exe and reused for every `serve`/`run` invocation.
public sealed class SupervisorConfig
{
    // Tool name used in console banners (e.g. "ky-ai-ng", "ky-ai-dotnet").
    public required string ToolName { get; init; }

    // Singular noun for a supervised app in console output (e.g. "frontend", "backend").
    public required string Noun { get; init; }

    // Default hub port if --hub isn't given (5101 for ky-ai-ng, 5102 for ky-ai-dotnet).
    public required int DefaultHubPort { get; init; }

    // Tool-specific build-output detection.
    public required IBuildMatcher Matcher { get; init; }

    // File extensions whose changes mark a build as pending (e.g. .ts/.html, .cs/.razor).
    public required IReadOnlyList<string> SourceExtensions { get; init; }

    // Extensions a hot reload swaps cleanly in place (templates/styles for ng). When a build
    // incorporates any file OUTSIDE this set (e.g. a .ts), already-created objects may still be
    // running the old code under HMR, so the verdict carries a `mayHaveStaleInstances` hint. Empty
    // (default) disables the hint — a tool opts in by listing its hot-swappable extensions.
    public IReadOnlyList<string> HotReloadSafeExtensions { get; init; } = Array.Empty<string>();

    // Path segments (e.g. "\\node_modules\\") that exclude a file from the source watcher.
    public required IReadOnlyList<string> WatchExcludeSegments { get; init; }

    // Picks the directory to watch from the working directory (e.g. Angular prefers the
    // `src` subtree; .NET watches the whole working dir).
    public required Func<string, string> WatchRoot { get; init; }

    // Default wait_for_build / restart timeout in ms (Angular 60s, .NET 90s — cold builds are slow).
    public int DefaultTimeoutMs { get; init; } = 60000;

    // Debounce quiet-window in ms used by restart/start (Angular 400, .NET 500).
    public int DefaultQuietMs { get; init; } = 500;

    // Resolves the file an `inject` request targets when no explicit file is given (ng → the app's
    // index.html). Null (default) means the supervisor has no inject target and /inject 400s — so the
    // generic inject mechanism is available only where a tool opts in (ng does; dotnet doesn't).
    public Func<string, string?>? ResolveInjectTarget { get; init; }
}

// Per-invocation runtime values for one `serve`/`run`, resolved from the command line by the
// exe and handed to SupervisorHost.RunAsync along with the static SupervisorConfig.
public sealed class SupervisorOptions
{
    public required string Name { get; init; }
    public required string WorkingDir { get; init; }
    public required string ChildFileName { get; init; }            // e.g. "node", "dotnet"
    public required IReadOnlyList<string> ChildArgs { get; init; }  // full arg list for the child
    public required string BannerCommand { get; init; }            // shown on the ↻ line, e.g. "ng serve"
    public string? LogPath { get; init; }                          // null → in-memory buffer only
    public int LogLines { get; init; } = 200;
    public int ControlPort { get; init; }                          // 0 → OS-assigned loopback port
    public required string HubUrl { get; init; }
    public bool UseHub { get; init; } = true;
    public bool AutostartHub { get; init; } = true;
    public IReadOnlyList<string>? AfterStart { get; init; }         // command to launch once up (null/empty → none)
}

// Tool-specific identity for the hub control plane.
public sealed class HubConfig
{
    public required string ToolName { get; init; }     // e.g. "ky-ai-ng", "ky-ai-dotnet"
    public required string Noun { get; init; }          // singular, e.g. "frontend"
    public required string NounPlural { get; init; }    // plural key in the list payload, e.g. "frontends"
    public required int DefaultPort { get; init; }      // 5101 / 5102
}
