using KY.AI.Net;
using KY.AI.Ng;
using KY.AI.Serve;
using Xunit;

namespace KY.AI.Serve.Tests;

// The tool-specific matchers: line classification (incl. the new warning kind) and the pure
// diagnostic-parsing helpers the BuildTracker correlates.
public class BuildMatcherTests
{
    // ── Angular / esbuild ──

    [Theory]
    [InlineData("Changes detected. Rebuilding...", LineKind.BuildStart)]
    [InlineData("✘ [ERROR] TS2304: Cannot find name 'foo'.", LineKind.Error)]
    [InlineData("▲ [WARNING] 'allowSignalWrites' is deprecated", LineKind.Warning)]
    [InlineData("Application bundle generation complete. [2.1 seconds]", LineKind.SettledSuccess)]
    [InlineData("Application bundle generation failed. [0.9 seconds]", LineKind.SettledFailed)]
    [InlineData("│ chunk-ABC.js | 2.34 kB | …and 714 more lazy chunks", LineKind.None)]
    public void Ng_classify(string line, LineKind expected)
        => Assert.Equal(expected, new NgBuildMatcher().Classify(line, building: true));

    [Fact]
    public void Ng_parses_error_header_without_location()
    {
        var d = new NgBuildMatcher().TryParseDiagnostic("✘ [ERROR] TS2304: Cannot find name 'foo'. [plugin angular-compiler]");

        Assert.NotNull(d);
        Assert.Equal("error", d!.Severity);
        Assert.Null(d.File);
        Assert.Contains("Cannot find name 'foo'", d.Message);
    }

    [Fact]
    public void Ng_parses_warning_header()
    {
        var d = new NgBuildMatcher().TryParseDiagnostic("▲ [WARNING] 'allowSignalWrites' is deprecated");
        Assert.Equal("warning", d!.Severity);
    }

    [Theory]
    [InlineData("    src/app/app.component.ts:12:34:", "src/app/app.component.ts", 12, 34)]
    [InlineData(@"    C:\repo\src\x.ts:1:5:", @"C:\repo\src\x.ts", 1, 5)]
    public void Ng_parses_location_line(string line, string file, int row, int col)
    {
        var loc = new NgBuildMatcher().TryParseLocation(line);
        Assert.NotNull(loc);
        Assert.Equal(file, loc!.Value.File);
        Assert.Equal(row, loc.Value.Line);
        Assert.Equal(col, loc.Value.Col);
    }

    [Fact]
    public void Ng_ignores_non_location_lines()
        => Assert.Null(new NgBuildMatcher().TryParseLocation("    at someStackFrame (thing.js:10:5)"));

    // ── .NET ──

    [Theory]
    [InlineData("dotnet watch ⌚ File changed: ./Program.cs", LineKind.BuildStart)]
    [InlineData("Now listening on: http://localhost:5000", LineKind.SettledSuccess)]
    [InlineData("Build FAILED.", LineKind.SettledFailed)]
    public void Dotnet_classify_unconditional(string line, LineKind expected)
        => Assert.Equal(expected, new DotnetBuildMatcher().Classify(line, building: true));

    [Fact]
    public void Dotnet_counts_diagnostics_only_while_building()
    {
        var m = new DotnetBuildMatcher();
        const string err = @"C:\proj\File.cs(12,34): error CS0103: msg";
        const string warn = @"C:\proj\File.cs(9,1): warning CS0168: msg";

        Assert.Equal(LineKind.Error, m.Classify(err, building: true));
        Assert.Equal(LineKind.None, m.Classify(err, building: false));
        Assert.Equal(LineKind.Warning, m.Classify(warn, building: true));
        Assert.Equal(LineKind.None, m.Classify(warn, building: false));
    }

    [Fact]
    public void Dotnet_parses_single_line_diagnostic_with_project_suffix_stripped()
    {
        var d = new DotnetBuildMatcher().TryParseDiagnostic(
            @"C:\proj\File.cs(12,34): error CS0103: The name 'foo' does not exist [C:\proj\proj.csproj]");

        Assert.NotNull(d);
        Assert.Equal("error", d!.Severity);
        Assert.Equal(@"C:\proj\File.cs", d.File);
        Assert.Equal(12, d.Line);
        Assert.Equal(34, d.Column);
        Assert.Equal("The name 'foo' does not exist", d.Message);
    }

    [Fact]
    public void Dotnet_parses_warning_severity()
    {
        var d = new DotnetBuildMatcher().TryParseDiagnostic(
            @"C:\proj\File.cs(9,1): warning CS0168: 'x' is declared but never used");
        Assert.Equal("warning", d!.Severity);
    }
}
