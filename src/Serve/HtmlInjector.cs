namespace KY.AI.Serve;

// Generic, reversible HTML injection. Wraps `content` in sentinel marker comments and inserts it at
// a DOM path (head/body) of an HTML document; Remove strips the marked block. The markers are the
// whole point: the dev-controlled ky-ai tool adds a capture <script> on start and this removes
// it on stop, and the supervisor strips any leftover on its own startup (self-heal). Pure + testable.
internal static class HtmlInjector
{
    public static string Begin(string marker) => $"<!-- {marker}:begin -->";
    public static string End(string marker) => $"<!-- {marker}:end -->";

    public static bool Contains(string html, string marker) =>
        html.Contains(Begin(marker), StringComparison.Ordinal);

    // Remove any existing marked block, then insert a fresh one at `path` (so re-applying replaces).
    // Returns null if `path` isn't a supported target (caller → 400).
    public static string? Apply(string html, string path, string content, string marker)
    {
        var cleaned = Remove(html, marker);
        var block = $"\n{Begin(marker)}{content}{End(marker)}\n";
        return InsertAt(cleaned, path, block);
    }

    // Strip every marked block (and the one surrounding newline Apply added around each).
    public static string Remove(string html, string marker)
    {
        var begin = Begin(marker);
        var end = End(marker);
        while (true)
        {
            var b = html.IndexOf(begin, StringComparison.Ordinal);
            if (b < 0) return html;
            var e = html.IndexOf(end, b, StringComparison.Ordinal);
            if (e < 0) return html.Remove(b); // unmatched begin — drop the dangling tail defensively

            var start = b > 0 && html[b - 1] == '\n' ? b - 1 : b;
            var stop = e + end.Length;
            if (stop < html.Length && html[stop] == '\n') stop++;
            html = html.Remove(start, stop - start);
        }
    }

    private static string? InsertAt(string html, string path, string block)
    {
        var p = path.Trim().Trim('/').ToLowerInvariant(); // "/html/head" -> "html/head"
        if (p.EndsWith("head", StringComparison.Ordinal)) return InsertInTag(html, "head", block);
        if (p.EndsWith("body", StringComparison.Ordinal)) return InsertInTag(html, "body", block);
        return null;
    }

    // Insert `block` as the last child of <tag> (before </tag>); fall back to just after the opening
    // <tag …>, then to appending to the document.
    private static string InsertInTag(string html, string tag, string block)
    {
        var close = html.IndexOf($"</{tag}>", StringComparison.OrdinalIgnoreCase);
        if (close >= 0) return html.Insert(close, block);
        var openEnd = TagOpenEnd(html, tag);
        if (openEnd >= 0) return html.Insert(openEnd, block);
        return html + block;
    }

    // Index just past the '>' of the opening <tag>, or -1. Skips lookalikes (e.g. <header> for head).
    private static int TagOpenEnd(string html, string tag)
    {
        var needle = "<" + tag;
        var i = 0;
        while (true)
        {
            var idx = html.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;
            var after = idx + needle.Length;
            var c = after < html.Length ? html[after] : '\0';
            if (c == '>') return after + 1;
            if (c is ' ' or '\t' or '\r' or '\n' or '/')
            {
                var gt = html.IndexOf('>', after);
                return gt >= 0 ? gt + 1 : -1;
            }
            i = after;
        }
    }
}
