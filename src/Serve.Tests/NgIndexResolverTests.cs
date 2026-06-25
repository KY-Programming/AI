using System.IO;
using KY.AI.Ng;
using Xunit;

namespace KY.AI.Serve.Tests;

// The angular.json index resolution that supplies ky-ai-ng's inject target.
public class NgIndexResolverTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory().FullName;
    private static string Norm(string? p) => System.IO.Path.GetFullPath(p!);

    private static void WriteHtml(string dir, string rel)
    {
        var full = System.IO.Path.Combine(dir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "<html><head></head></html>");
    }

    [Fact]
    public void Falls_back_to_src_index_when_no_angular_json()
    {
        var dir = TempDir();
        WriteHtml(dir, "src/index.html");
        Assert.Equal(Norm(System.IO.Path.Combine(dir, "src", "index.html")), Norm(NgIndexResolver.Resolve(dir)));
    }

    [Fact]
    public void Returns_null_when_nothing_exists() => Assert.Null(NgIndexResolver.Resolve(TempDir()));

    [Fact]
    public void Reads_string_index_from_angular_json()
    {
        var dir = TempDir();
        WriteHtml(dir, "projects/app/index.html");
        File.WriteAllText(System.IO.Path.Combine(dir, "angular.json"),
            """{ "projects": { "app": { "architect": { "build": { "options": { "index": "projects/app/index.html" } } } } } }""");
        Assert.Equal(Norm(System.IO.Path.Combine(dir, "projects", "app", "index.html")), Norm(NgIndexResolver.Resolve(dir)));
    }

    [Fact]
    public void Reads_object_index_input_under_targets_key()
    {
        var dir = TempDir();
        WriteHtml(dir, "src/custom-index.html");
        File.WriteAllText(System.IO.Path.Combine(dir, "angular.json"),
            """{ "projects": { "app": { "targets": { "build": { "options": { "index": { "input": "src/custom-index.html", "output": "index.html" } } } } } } }""");
        Assert.Equal(Norm(System.IO.Path.Combine(dir, "src", "custom-index.html")), Norm(NgIndexResolver.Resolve(dir)));
    }

    [Fact]
    public void Honors_default_project_first()
    {
        var dir = TempDir();
        WriteHtml(dir, "a/index.html");
        WriteHtml(dir, "b/index.html");
        File.WriteAllText(System.IO.Path.Combine(dir, "angular.json"),
            """
            { "defaultProject": "b",
              "projects": {
                "a": { "architect": { "build": { "options": { "index": "a/index.html" } } } },
                "b": { "architect": { "build": { "options": { "index": "b/index.html" } } } } } }
            """);
        Assert.Equal(Norm(System.IO.Path.Combine(dir, "b", "index.html")), Norm(NgIndexResolver.Resolve(dir)));
    }

    [Fact]
    public void Falls_back_when_angular_json_is_malformed()
    {
        var dir = TempDir();
        WriteHtml(dir, "src/index.html");
        File.WriteAllText(System.IO.Path.Combine(dir, "angular.json"), "{ not valid json");
        Assert.Equal(Norm(System.IO.Path.Combine(dir, "src", "index.html")), Norm(NgIndexResolver.Resolve(dir)));
    }
}
