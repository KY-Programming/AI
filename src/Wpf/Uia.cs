using System.Collections.Concurrent;

namespace KY.AI.Wpf;

// Every UI Automation call in this process runs on one dedicated MTA thread.
//
// Two reasons, both load-bearing. UIA is COM underneath, and the managed client is only well-behaved
// from an MTA apartment — an STA caller with no message pump can deadlock against the provider it is
// asking. And a single thread serialises the work, so two agents driving the same window (which the
// hub allows: it is one endpoint for every caller) can never interleave a tree walk with another's
// pattern call and read a half-updated tree.
//
// Nothing here touches the target app's UI thread: UIA marshals the request into the provider, which
// answers on its own terms. That is what makes reads focus-free.
//
// This is also where actions are made focus-free, which they are not by default — see Automation.
internal static class Uia
{
    private static readonly BlockingCollection<Action> Queue = new();

    private static IUIAutomation2? automation;
    private static IUIAutomationTreeWalker? controlView;

    // The one UI Automation client in the process, with AutoSetFocus turned OFF.
    //
    // With it on — which is the default, and the only setting System.Windows.Automation has — UIA
    // focuses the target element before every pattern call, and focusing an element in another
    // process ACTIVATES ITS WINDOW. Setting a text box's value on an app the user was not looking at
    // pulled that app to the front, mid-sentence, from a tool whose whole promise is that it does
    // not. It is the client that does this, not the app: the same call raises Notepad.
    //
    // Created lazily, which means on the automation thread — every caller reaches it from inside
    // RunAsync — so the object is made in the MTA it will be used from.
    public static IUIAutomation2 Automation => automation ??= Create();

    // ControlView, not RawView: RawView exposes every WPF layout panel — Border, ContentPresenter,
    // Grid — and would multiply the tree (and the agent's token bill) with nodes that can never be
    // targeted. ControlView is what a screen reader sees, which is the same set a test wants.
    public static IUIAutomationTreeWalker ControlView => controlView ??= Automation.get_ControlViewWalker();

    private static IUIAutomation2 Create()
    {
        var type = Type.GetTypeFromCLSID(UiaIds.CUIAutomation8)
                   ?? throw new InvalidOperationException("UI Automation (CUIAutomation8) is not registered on this machine.");
        var uia = (IUIAutomation2)Activator.CreateInstance(type)!;
        uia.put_AutoSetFocus(false);

        // Read back rather than trust: if this ever silently failed, the tool would go back to
        // stealing the user's foreground on every action — the one failure it must not have. Better
        // to refuse to automate at all than to automate at their expense.
        if (uia.get_AutoSetFocus())
            throw new InvalidOperationException(
                "UI Automation refused to turn AutoSetFocus off, so every action would raise the target " +
                "window. Refusing to continue.");

        return uia;
    }

    static Uia()
    {
        var worker = new Thread(Pump)
        {
            IsBackground = true,
            Name = "ky-ai-wpf-uia",
        };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    // Run `work` on the automation thread. Callers are ASP.NET Core request threads, so this hands
    // back a Task rather than blocking one of them on a cross-thread wait.
    public static Task<T> RunAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private static void Pump()
    {
        foreach (var work in Queue.GetConsumingEnumerable())
        {
            // A faulted item already completed its own TaskCompletionSource; the pump must survive it
            // or every later call would hang waiting on a thread that quietly died.
            try { work(); }
            catch { /* handed to the caller's task above */ }
        }
    }
}
