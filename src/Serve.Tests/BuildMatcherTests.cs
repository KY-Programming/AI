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

    // ── Angular / webpack (`:browser` builder, lines as captured from Angular CLI 18) ──

    [Theory]
    [InlineData("√ Compiled successfully.", LineKind.SettledSuccess)]
    [InlineData("× Failed to compile.", LineKind.SettledFailed)]
    [InlineData("Error: src/app/services/app.service.ts:7:35 - error TS2307: Cannot find module '@xws/ui' or its corresponding type declarations.", LineKind.Error)]
    [InlineData("Warning: src/app/app.component.ts:9:29 - warning NG8107: The left side of this optional chain operation does not include 'null' or 'undefined' in its type.", LineKind.Warning)]
    [InlineData("./node_modules/@syncfusion/ej2-angular-grids/fesm2020/syncfusion-ej2-angular-grids.mjs:527:50-54 - Error: export 'Grid' (imported as 'Grid') was not found in '@syncfusion/ej2-grids' (module has no exports)", LineKind.Error)]
    [InlineData("./src/styles.css - Error: Module build failed (from ./node_modules/css-loader/dist/cjs.js):", LineKind.Error)]
    [InlineData("Build at: 2026-10-01T15:38:09.690Z - Hash: b8fe39c7f216f4e7 - Time: 131ms", LineKind.None)]
    [InlineData("- Generating browser application bundles (phase: setup)...", LineKind.None)]
    public void Ng_webpack_classify(string line, LineKind expected)
        => Assert.Equal(expected, new NgBuildMatcher().Classify(line, building: true));

    [Fact]
    public void Ng_webpack_bundle_complete_opens_a_cycle_but_never_settles_it()
    {
        var m = new NgBuildMatcher();
        Assert.Equal(LineKind.BuildStart, m.Classify("√ Browser application bundle generation complete.", building: false));
        Assert.Equal(LineKind.None, m.Classify("√ Browser application bundle generation complete.", building: true));
    }

    [Fact]
    public void Ng_webpack_bare_error_line_counts_only_after_a_blank_line()
    {
        var m = new NgBuildMatcher();
        Assert.Equal(LineKind.None, m.Classify("", building: true));
        Assert.Equal(LineKind.Error, m.Classify("Error: bundle initial exceeded maximum budget.", building: true));
        // A module error's continuation ("Module build failed (…):" ⏎ "Error: Can't resolve …").
        Assert.Equal(LineKind.Error, m.Classify("./src/styles.css - Error: Module build failed (from ./node_modules/css-loader/dist/cjs.js):", building: true));
        Assert.Equal(LineKind.None, m.Classify("Error: Can't resolve 'missing.css' in 'C:\\repo\\src'", building: true));
    }

    [Fact]
    public void Ng_parses_webpack_compiler_diagnostic_with_inline_location()
    {
        var d = new NgBuildMatcher().TryParseDiagnostic(
            "Error: src/app/services/app.service.ts:41:22 - error NG2003: No suitable injection token for parameter 'localizationService' of class 'AppService'.");

        Assert.NotNull(d);
        Assert.Equal("error", d!.Severity);
        Assert.Equal("src/app/services/app.service.ts", d.File);
        Assert.Equal(41, d.Line);
        Assert.Equal(22, d.Column);
        Assert.StartsWith("NG2003: No suitable injection token", d.Message);
    }

    [Fact]
    public void Ng_parses_webpack_module_diagnostic_with_column_range()
    {
        var d = new NgBuildMatcher().TryParseDiagnostic(
            "./node_modules/@syncfusion/ej2-angular-grids/fesm2020/syncfusion-ej2-angular-grids.mjs:527:50-54 - Error: export 'Grid' (imported as 'Grid') was not found in '@syncfusion/ej2-grids' (module has no exports)");

        Assert.NotNull(d);
        Assert.Equal("error", d!.Severity);
        Assert.Equal("./node_modules/@syncfusion/ej2-angular-grids/fesm2020/syncfusion-ej2-angular-grids.mjs", d.File);
        Assert.Equal(527, d.Line);
        Assert.Equal(50, d.Column);
        Assert.StartsWith("export 'Grid'", d.Message);
    }

    [Fact]
    public void Ng_parses_webpack_module_diagnostic_without_location()
    {
        var d = new NgBuildMatcher().TryParseDiagnostic(
            "./src/styles.css - Error: Module build failed (from ./node_modules/css-loader/dist/cjs.js):");

        Assert.Equal("./src/styles.css", d!.File);
        Assert.Null(d.Line);
        Assert.StartsWith("Module build failed", d.Message);
    }

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
