namespace KY.AI.Terminal;

// Per-invocation values for one shared terminal session, resolved from the command line.
internal sealed class TerminalSessionOptions
{
    public required string Name { get; init; }
    public required string ShellDisplay { get; init; }   // e.g. "pwsh", "ssh"
    public required string ExePath { get; init; }
    public required string CommandLine { get; init; }     // full CreateProcess command line
    public required string WorkingDir { get; init; }
    public int Scrollback { get; init; } = 5000;
    public TerminalMode InitialMode { get; init; } = TerminalMode.ReadOnly;
    public byte PrefixByte { get; init; } = 0x02;          // Ctrl+B
    public string PrefixName { get; init; } = "Ctrl+B";
    public string? AuditPath { get; init; }                // null → in-memory audit only
    public bool Tui { get; init; } = true;                 // false (--no-tui) → plain passthrough
}
