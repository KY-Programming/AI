using System.Runtime.CompilerServices;

// The test project exercises the internal console buffer + injection helper directly
// (ConsoleEventLog, ConsoleInjection, ConsoleLevels) without those becoming part of the public API.
[assembly: InternalsVisibleTo("KY.AI.Browser.Tests")]
