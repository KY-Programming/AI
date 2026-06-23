using System.Runtime.CompilerServices;

// The terminal tool (ky-ai-terminal) reuses the internal control-plane plumbing — Hub forwarding,
// the JobObject tree-kill, and the RollingLog buffer — without those types having to become part
// of Serve's public surface. Keep this in sync with the exe's AssemblyName.
[assembly: InternalsVisibleTo("ky-ai-terminal")]
