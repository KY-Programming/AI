using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// The test project exercises the pure pieces directly — window name resolution (WindowMatcher),
// selector matching (ElementSelector), the toggle-state spellings, the bare-root hint and the
// capture path helpers — without those becoming part of the public API.
[assembly: InternalsVisibleTo("KY.AI.Wpf.Tests")]

// UI Automation, GDI and COM activation are Windows APIs, so this whole assembly is Windows-only —
// declared once here rather than annotated call site by call site, and rather than silenced with
// NoWarn, which would also hide a genuinely misplaced call.
//
// It is an annotation, not an enforcement: the target framework stays plain `net10.0` because
// PackAsTool rejects a `-windows` one, and every tool in this suite ships as a .NET global tool.
// ky-ai-terminal does the same thing for the same reason (ConPTY). `Program.Main` makes the
// promise good at runtime by refusing to start anywhere else.
[assembly: SupportedOSPlatform("windows")]
