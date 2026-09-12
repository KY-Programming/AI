using System.Runtime.InteropServices;

namespace KY.AI.Wpf;

// The UI Automation COM client, declared by hand.
//
// WHY NOT System.Windows.Automation: the managed client focuses the target element before every
// pattern call, which activates its window — cross-process, on an app the user was not looking at.
// That is precisely the theft this tool exists to avoid, and it is not switchable there. The COM
// client is, via IUIAutomation2.AutoSetFocus (see Uia.Automation).
//
// It has to be ALL of the client, not just the write path: once System.Windows.Automation has been
// used anywhere in the process, AutoSetFocus=false stops working — measured, in both orders and on
// separate threads. So nothing in this assembly may reference the managed client again; a test
// enforces that (NoSyntheticInputTests).
//
// Hand-rolled rather than a NuGet interop assembly: the surface below is the whole of what the tool
// uses, and it keeps a published tool free of a third-party dependency for six method calls.
//
// HOW TO READ THE INTERFACES: a COM interface is its vtable, so every method must be declared in
// the exact order of the IDL, including the ones we never call. Those are the `_Unused*` slots —
// they exist to occupy their position and must never be invoked. Adding a method means inserting it
// at its IDL position, never appending it.

[ComImport, Guid("34723AFF-0C9D-49D0-9896-7AB52DF8CD8A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation2
{
    // ── IUIAutomation ──
    void _UnusedCompareElements();
    void _UnusedCompareRuntimeIds();
    void _UnusedGetRootElement();

    [return: MarshalAs(UnmanagedType.Interface)]
    IUIAutomationElement ElementFromHandle(IntPtr hwnd);

    void _UnusedElementFromPoint();
    void _UnusedGetFocusedElement();
    void _UnusedGetRootElementBuildCache();
    void _UnusedElementFromHandleBuildCache();
    void _UnusedElementFromPointBuildCache();
    void _UnusedGetFocusedElementBuildCache();
    void _UnusedCreateTreeWalker();

    [return: MarshalAs(UnmanagedType.Interface)]
    IUIAutomationTreeWalker get_ControlViewWalker();

    void _UnusedGetContentViewWalker();
    void _UnusedGetRawViewWalker();
    void _UnusedGetRawViewCondition();
    void _UnusedGetControlViewCondition();
    void _UnusedGetContentViewCondition();
    void _UnusedCreateCacheRequest();
    void _UnusedCreateTrueCondition();
    void _UnusedCreateFalseCondition();
    void _UnusedCreatePropertyCondition();
    void _UnusedCreatePropertyConditionEx();
    void _UnusedCreateAndCondition();
    void _UnusedCreateAndConditionFromArray();
    void _UnusedCreateAndConditionFromNativeArray();
    void _UnusedCreateOrCondition();
    void _UnusedCreateOrConditionFromArray();
    void _UnusedCreateOrConditionFromNativeArray();
    void _UnusedCreateNotCondition();
    void _UnusedAddAutomationEventHandler();
    void _UnusedRemoveAutomationEventHandler();
    void _UnusedAddPropertyChangedEventHandlerNativeArray();
    void _UnusedAddPropertyChangedEventHandler();
    void _UnusedRemovePropertyChangedEventHandler();
    void _UnusedAddStructureChangedEventHandler();
    void _UnusedRemoveStructureChangedEventHandler();
    void _UnusedAddFocusChangedEventHandler();
    void _UnusedRemoveFocusChangedEventHandler();
    void _UnusedRemoveAllEventHandlers();
    void _UnusedIntNativeArrayToSafeArray();
    void _UnusedIntSafeArrayToNativeArray();
    void _UnusedRectToVariant();
    void _UnusedVariantToRect();
    void _UnusedSafeArrayToRectNativeArray();
    void _UnusedCreateProxyFactoryEntry();
    void _UnusedGetProxyFactoryMapping();
    void _UnusedGetPropertyProgrammaticName();
    void _UnusedGetPatternProgrammaticName();
    void _UnusedPollForPotentialSupportedPatterns();
    void _UnusedPollForPotentialSupportedProperties();
    void _UnusedCheckNotSupported();
    void _UnusedGetReservedNotSupportedValue();
    void _UnusedGetReservedMixedAttributeValue();
    void _UnusedElementFromIAccessible();
    void _UnusedElementFromIAccessibleBuildCache();

    // ── IUIAutomation2 ── the reason this file exists.
    [return: MarshalAs(UnmanagedType.Bool)] bool get_AutoSetFocus();
    void put_AutoSetFocus([MarshalAs(UnmanagedType.Bool)] bool value);
}

[ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    // Present because it is vtable slot 0, never called: focusing an element is what this tool
    // refuses to do.
    void _UnusedSetFocus();

    void _UnusedGetRuntimeId();
    void _UnusedFindFirst();
    void _UnusedFindAll();
    void _UnusedFindFirstBuildCache();
    void _UnusedFindAllBuildCache();
    void _UnusedBuildUpdatedCache();
    void _UnusedGetCurrentPropertyValue();
    void _UnusedGetCurrentPropertyValueEx();
    void _UnusedGetCachedPropertyValue();
    void _UnusedGetCachedPropertyValueEx();
    void _UnusedGetCurrentPatternAs();
    void _UnusedGetCachedPatternAs();

    // Null when the element does not support the pattern — the COM spelling of TryGetCurrentPattern.
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object? GetCurrentPattern(int patternId);

    void _UnusedGetCachedPattern();
    void _UnusedGetCachedParent();
    void _UnusedGetCachedChildren();
    void _UnusedGetCurrentProcessId();

    int get_CurrentControlType();

    void _UnusedGetCurrentLocalizedControlType();

    [return: MarshalAs(UnmanagedType.BStr)] string get_CurrentName();

    void _UnusedGetCurrentAcceleratorKey();
    void _UnusedGetCurrentAccessKey();
    void _UnusedGetCurrentHasKeyboardFocus();
    void _UnusedGetCurrentIsKeyboardFocusable();

    [return: MarshalAs(UnmanagedType.Bool)] bool get_CurrentIsEnabled();

    [return: MarshalAs(UnmanagedType.BStr)] string get_CurrentAutomationId();

    void _UnusedGetCurrentClassName();
    void _UnusedGetCurrentHelpText();
    void _UnusedGetCurrentCulture();
    void _UnusedGetCurrentIsControlElement();
    void _UnusedGetCurrentIsContentElement();
    void _UnusedGetCurrentIsPassword();
    void _UnusedGetCurrentNativeWindowHandle();
    void _UnusedGetCurrentItemType();

    [return: MarshalAs(UnmanagedType.Bool)] bool get_CurrentIsOffscreen();

    void _UnusedGetCurrentOrientation();
    void _UnusedGetCurrentFrameworkId();
    void _UnusedGetCurrentIsRequiredForForm();
    void _UnusedGetCurrentItemStatus();

    UiaRect get_CurrentBoundingRectangle();
}

[ComImport, Guid("4042C624-389C-4AFC-A630-9DF854A541FC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTreeWalker
{
    void _UnusedGetParentElement();

    [return: MarshalAs(UnmanagedType.Interface)]
    IUIAutomationElement? GetFirstChildElement([MarshalAs(UnmanagedType.Interface)] IUIAutomationElement element);

    void _UnusedGetLastChildElement();

    [return: MarshalAs(UnmanagedType.Interface)]
    IUIAutomationElement? GetNextSiblingElement([MarshalAs(UnmanagedType.Interface)] IUIAutomationElement element);
}

[ComImport, Guid("A94CD8B1-0844-4CD6-9D2D-640537AB39E9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationValuePattern
{
    void SetValue([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string get_CurrentValue();
    [return: MarshalAs(UnmanagedType.Bool)] bool get_CurrentIsReadOnly();
}

[ComImport, Guid("FB377FBE-8EA6-46D5-9C73-6499642D3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationInvokePattern
{
    void Invoke();
}

[ComImport, Guid("94CF8058-9B8D-4AB9-8BFD-4CD0A33C8C70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTogglePattern
{
    void Toggle();
    ToggleState get_CurrentToggleState();
}

[ComImport, Guid("619BE086-1F4E-4EE4-BAFA-210128738730"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationExpandCollapsePattern
{
    void Expand();
    void Collapse();
    ExpandCollapseState get_CurrentExpandCollapseState();
}

[ComImport, Guid("A8EFA66A-0FDA-421A-9194-38021F3578EA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationSelectionItemPattern
{
    void Select();
    void AddToSelection();
    void _UnusedRemoveFromSelection();
    [return: MarshalAs(UnmanagedType.Bool)] bool get_CurrentIsSelected();
}

[ComImport, Guid("B488300F-D015-4F19-9C29-BB595E3645EF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationScrollItemPattern
{
    void ScrollIntoView();
}

// A Win32 RECT of four LONGs — NOT the double-based UiaRect from the provider side. Getting that
// wrong does not fail: the four ints land in the first half of an oversized struct and every element
// reports a nonsense rectangle.
[StructLayout(LayoutKind.Sequential)]
internal struct UiaRect
{
    public int Left, Top, Right, Bottom;
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    // An element with no on-screen rectangle (offscreen, or a provider that does not place it)
    // reports all zeros rather than an error.
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

internal enum ToggleState { Off = 0, On = 1, Indeterminate = 2 }

internal enum ExpandCollapseState { Collapsed = 0, Expanded = 1, PartiallyExpanded = 2, LeafNode = 3 }

// The constants the interfaces above are addressed with. Spelled out rather than pulled from
// UIAutomationTypes, so that assembly — and the managed client that comes with it — stays out of
// the process for good.
internal static class UiaIds
{
    // CUIAutomation8. Deliberately NOT CUIAutomation: that one only offers IUIAutomation, which has
    // no AutoSetFocus, and a client that cannot switch the auto-focus off has no business here.
    public static readonly Guid CUIAutomation8 = new("E22AD333-B25F-460C-83D0-0581107395C9");

    public const int InvokePattern = 10000;
    public const int ValuePattern = 10002;
    public const int ExpandCollapsePattern = 10005;
    public const int SelectionItemPattern = 10010;
    public const int TogglePattern = 10015;
    public const int ScrollItemPattern = 10017;

    // UIA's own error codes. The managed client turned these into typed exceptions; here they arrive
    // as COMException.HResult, and the tools tell them apart to say something useful about each.
    public const int ElementNotAvailable = unchecked((int)0x80040201);
    public const int ElementNotEnabled = unchecked((int)0x80040200);
    public const int NotSupported = unchecked((int)0x80040204);
    public const int AccessDenied = unchecked((int)0x80070005);

    // Not all of them arrive as COMException: UIA_E_INVALIDOPERATION is 0x80131509, which is the
    // CLR's own COR_E_INVALIDOPERATION, so the interop layer turns it into a plain
    // InvalidOperationException before anything here sees an HRESULT. It is an ordinary provider
    // refusal — a control saying "not right now" — and every catch around a pattern call has to list
    // it beside COMException or it escapes as something far more alarming.

    // ControlType ids to the names `tree`/`find` print and a selector matches on. This is the
    // programmatic name (UIA's own, locale-independent) — NOT the localized one, which would make a
    // selector that works on a German machine fail on an English one.
    private static readonly Dictionary<int, string> ControlTypes = new()
    {
        [50000] = "Button", [50001] = "Calendar", [50002] = "CheckBox", [50003] = "ComboBox",
        [50004] = "Edit", [50005] = "Hyperlink", [50006] = "Image", [50007] = "ListItem",
        [50008] = "List", [50009] = "Menu", [50010] = "MenuBar", [50011] = "MenuItem",
        [50012] = "ProgressBar", [50013] = "RadioButton", [50014] = "ScrollBar", [50015] = "Slider",
        [50016] = "Spinner", [50017] = "StatusBar", [50018] = "Tab", [50019] = "TabItem",
        [50020] = "Text", [50021] = "ToolBar", [50022] = "ToolTip", [50023] = "Tree",
        [50024] = "TreeItem", [50025] = "Custom", [50026] = "Group", [50027] = "Thumb",
        [50028] = "DataGrid", [50029] = "DataItem", [50030] = "Document", [50031] = "SplitButton",
        [50032] = "Window", [50033] = "Pane", [50034] = "Header", [50035] = "HeaderItem",
        [50036] = "Table", [50037] = "TitleBar", [50038] = "Separator", [50039] = "SemanticZoom",
        [50040] = "AppBar",
    };

    // A provider is free to report an id from a newer Windows than this table knows. Saying the
    // number is more use to an agent than a blanket "Custom", which is itself a real control type.
    public static string ControlTypeName(int id) =>
        ControlTypes.TryGetValue(id, out var name) ? name : $"Unknown({id})";
}
