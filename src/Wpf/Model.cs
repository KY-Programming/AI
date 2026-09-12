namespace KY.AI.Wpf;

// A top-level window the tool can see. Plain data — produced by WindowFinder from the window
// manager, consumed by WindowMatcher's (pure, testable) name resolution.
public sealed record WindowInfo(
    long Handle,
    int Pid,
    string Process,
    string Title,
    bool Minimized,
    // Set when this is an owned window — a modal dialog, typically. It is a real window an agent can
    // target directly; the owner is carried so the reply says whose dialog it is.
    long? OwnerHandle,
    string? OwnerTitle,
    int X,
    int Y,
    int Width,
    int Height);

// One node of a window's automation tree.
//
// `Path` is the node's position as dot-separated child indices from the subtree root ("" is the root
// itself, "2.0" is the first child of the third child). It is what `tree`/`find` hand back and what
// the action tools accept, so an agent can act on exactly the node it just read instead of
// re-describing it. It is only valid while the tree keeps its shape — after a filter or a reload,
// read again rather than reusing a path.
//
// Nulls are omitted from the JSON (Json.Options): a full WPF tree is mostly empty fields, and the
// agent pays for every token of it.
public sealed record NodeInfo(
    string Path,
    int Depth,
    string Type,
    string? AutomationId,
    string? Name,
    string? Value,
    bool Enabled,
    bool Offscreen,
    int[]? Rect,
    string[]? Patterns,
    int Children);
