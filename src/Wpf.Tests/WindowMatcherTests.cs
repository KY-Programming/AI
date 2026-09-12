using KY.AI.Wpf;
using Xunit;

namespace KY.AI.Wpf.Tests;

// Window resolution is the one place where a wrong answer is expensive: every other tool then acts
// on somebody else's window. So the tiers are pinned here, and so is the refusal to guess.
public class WindowMatcherTests
{
    private static WindowInfo Win(string process, string title, long handle = 1, int pid = 100) =>
        new(handle, pid, process, title, false, null, null, 0, 0, 800, 600);

    private static WindowInfo Dialog(string process, string title, long handle, int pid, long ownerHandle) =>
        new(handle, pid, process, title, false, ownerHandle, "owner", 0, 0, 400, 300);

    private static readonly List<WindowInfo> Desktop =
    [
        Win("KY.ProjectHub", "Project Hub", 10, 15672),
        Win("chrome", "Project - KY-Programming - Google Chrome", 20, 47720),
        Win("chrome", "Twitch - Google Chrome", 21, 47720),
        Win("rider64", "ProjectHub - MainWindow.xaml", 30, 58196),
    ];

    [Fact]
    public void Resolves_by_exact_process_name()
    {
        var r = WindowMatcher.Resolve(Desktop, "KY.ProjectHub");
        Assert.Equal(10, r.Window!.Handle);
    }

    [Fact]
    public void Process_name_is_case_insensitive_and_tolerates_the_exe_suffix()
    {
        Assert.Equal(10, WindowMatcher.Resolve(Desktop, "ky.projecthub").Window!.Handle);
        Assert.Equal(10, WindowMatcher.Resolve(Desktop, "KY.ProjectHub.exe").Window!.Handle);
    }

    [Fact]
    public void Resolves_by_exact_title()
    {
        var r = WindowMatcher.Resolve(Desktop, "Twitch - Google Chrome");
        Assert.Equal(21, r.Window!.Handle);
    }

    [Fact]
    public void Resolves_by_handle_and_pid()
    {
        Assert.Equal(30, WindowMatcher.Resolve(Desktop, "#30").Window!.Handle);
        Assert.Equal(30, WindowMatcher.Resolve(Desktop, "pid:58196").Window!.Handle);
    }

    // "ProjectHub" is a substring of a process name AND of Rider's title. The process tier wins, so
    // naming your app gets your app — not the editor that happens to have its file open.
    [Fact]
    public void Process_match_beats_a_title_match_elsewhere()
    {
        var r = WindowMatcher.Resolve(Desktop, "ProjectHub");
        Assert.Equal(10, r.Window!.Handle);
    }

    // An app with a modal dialog open matches the process tier twice — but one owns the other, so
    // naming the app means the app's own window. Without this, `list` showing dialogs (which it must,
    // or an agent cannot find a password prompt at all) would break every by-process call for as long
    // as any dialog is up.
    [Fact]
    public void An_app_and_its_own_dialog_resolve_to_the_app_window()
    {
        var desktop = new List<WindowInfo>
        {
            Win("KY.ProjectHub", "Project Hub", 10, 15672),
            Dialog("KY.ProjectHub", "Password", 11, 15672, ownerHandle: 10),
        };
        Assert.Equal(10, WindowMatcher.Resolve(desktop, "KY.ProjectHub").Window!.Handle);
    }

    // …and the dialog stays reachable on its own title, which is the whole point of listing it.
    [Fact]
    public void The_dialog_is_still_targetable_by_its_own_title()
    {
        var desktop = new List<WindowInfo>
        {
            Win("KY.ProjectHub", "Project Hub", 10, 15672),
            Dialog("KY.ProjectHub", "Password", 11, 15672, ownerHandle: 10),
        };
        Assert.Equal(11, WindowMatcher.Resolve(desktop, "Password").Window!.Handle);
    }

    // The owner/owned tie-break above resolves that case ONLY. Two independent windows of one process
    // are a real ambiguity and must still be refused rather than silently resolved to one of them.
    [Fact]
    public void Refuses_when_a_tier_matches_several_and_lists_them()
    {
        var r = WindowMatcher.Resolve(Desktop, "chrome");
        Assert.Null(r.Window);
        Assert.Contains("matches 2 windows", r.Error);
        Assert.Equal(2, r.Candidates.Count);
    }

    [Fact]
    public void Reports_no_match_with_the_full_desktop_as_candidates()
    {
        var r = WindowMatcher.Resolve(Desktop, "nothing-like-this");
        Assert.Null(r.Window);
        Assert.Contains("no window matches", r.Error);
        Assert.Equal(Desktop.Count, r.Candidates.Count);
    }

    // The suite-wide "omit the name when there is only one" shorthand — which on a desktop is only
    // safe when there really is exactly one.
    [Fact]
    public void Omitted_name_takes_the_only_window()
    {
        var one = new List<WindowInfo> { Win("KY.ProjectHub", "Project Hub", 10) };
        Assert.Equal(10, WindowMatcher.Resolve(one, null).Window!.Handle);
    }

    [Fact]
    public void Omitted_name_refuses_when_several_windows_are_visible()
    {
        var r = WindowMatcher.Resolve(Desktop, null);
        Assert.Null(r.Window);
        Assert.Contains("more than one", r.Error);
    }

    [Fact]
    public void Omitted_name_on_an_empty_desktop_says_so()
    {
        var r = WindowMatcher.Resolve([], null);
        Assert.Null(r.Window);
        Assert.Contains("no windows", r.Error);
    }
}
