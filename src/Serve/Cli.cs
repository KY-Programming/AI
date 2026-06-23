using System.Text;

namespace KY.AI.Serve;

// Small console/file helpers shared by both tools' entry points.
public static class Cli
{
    public static void TrySetUtf8Console()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected console */ }
    }

    public static void EnsureDir(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }
}
