using System.Reflection;
using System.Runtime.InteropServices;
using KY.AI.Wpf;
using Xunit;

namespace KY.AI.Wpf.Tests;

// The whole reason ky-ai-wpf exists is that it does NOT synthesise input or take the foreground: an
// agent testing a WPF app should never type into whatever the user has in front of them, move their
// caret or overwrite their clipboard.
//
// That promise is a property of the binary, not of anyone's good intentions, so it is checked
// mechanically here: the compiled assembly must not import a single Win32 function that could break
// it. A future change that reaches for SendInput "just for this one keyboard-only case" fails this
// test, which is the moment to have the conversation instead of shipping it.
public class NoSyntheticInputTests
{
    // Everything that can type, click, move the pointer, or push a window to the front.
    private static readonly string[] Forbidden =
    [
        // keyboard / mouse synthesis
        "SendInput", "keybd_event", "mouse_event", "SendMessage", "PostMessage", "SendMessageW",
        "PostMessageW", "SendMessageA", "PostMessageA",
        // pointer
        "SetCursorPos", "mouse_event", "SendMessageTimeout",
        // focus / activation
        "SetForegroundWindow", "SetActiveWindow", "SetFocus", "BringWindowToTop", "SwitchToThisWindow",
        "ShowWindow", "AllowSetForegroundWindow", "AttachThreadInput", "SetWindowPos",
        // clipboard
        "OpenClipboard", "SetClipboardData", "EmptyClipboard",
    ];

    private static IEnumerable<(Type Type, MethodInfo Method, DllImportAttribute Import)> Imports()
    {
        var assembly = typeof(Engine).Assembly;
        foreach (var type in assembly.GetTypes())
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var import = method.GetCustomAttribute<DllImportAttribute>();
            if (import is not null) yield return (type, method, import);
        }
    }

    [Fact]
    public void The_assembly_imports_no_input_synthesis_or_focus_stealing_win32_call()
    {
        var offenders = Imports()
            .Select(i => (i.Type.Name, Entry: i.Import.EntryPoint ?? i.Method.Name))
            .Where(i => Forbidden.Contains(i.Entry, StringComparer.Ordinal))
            .Select(i => $"{i.Name}.{i.Entry}")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "ky-ai-wpf must never synthesise input or take the foreground, but the assembly imports: " +
            string.Join(", ", offenders));
    }

    // The other half of the promise, and the one that is easy to break by accident.
    //
    // System.Windows.Automation focuses the element it acts on before every pattern call, which
    // activates that element's window across processes — the exact theft this tool exists to avoid,
    // and not switchable there. The COM client is (IUIAutomation2.AutoSetFocus, see Uia.Automation),
    // which is why the whole client surface is hand-rolled in UiaCom.cs.
    //
    // It has to be all of it: once the managed client has been used ANYWHERE in the process,
    // AutoSetFocus=false silently stops working — measured, in both orders and on separate threads.
    // So one `using System.Windows.Automation;` added for one convenient property re-breaks focus
    // freedom everywhere, with nothing failing except a user's foreground window. This test is the
    // only thing standing between that and a release.
    [Fact]
    public void The_assembly_does_not_reference_the_managed_ui_automation_client()
    {
        var referenced = typeof(Engine).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("UIAutomation", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(referenced.Length == 0,
            "ky-ai-wpf must drive UI Automation through the COM client with AutoSetFocus off, but the " +
            "assembly references the managed client: " + string.Join(", ", referenced) +
            ". Using it anywhere in the process makes every action raise the target window.");
    }

    // The positive half: it should still be importing the read-only window-manager calls it needs,
    // so this test cannot quietly start passing because the P/Invokes moved somewhere it stopped
    // looking.
    [Fact]
    public void The_read_only_window_calls_are_still_imported_where_the_scan_can_see_them()
    {
        var entries = Imports().Select(i => i.Import.EntryPoint ?? i.Method.Name).ToArray();
        Assert.Contains("EnumWindows", entries);
        Assert.Contains("GetForegroundWindow", entries);   // read to REPORT focus, never to change it
        Assert.Contains("PrintWindow", entries);           // capture without raising the window
    }
}
