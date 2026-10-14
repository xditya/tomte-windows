using Windows.Win32.UI.Accessibility;

namespace Tomte.AI.Automation;

/// <summary>A visible top-level window, in z-order.</summary>
internal sealed record UiaWindow(nint Handle, int ProcessId, string Title);

/// <summary>A process's file name (null when Windows would not say), and whether it runs elevated (null when that could not be checked).</summary>
internal sealed record UiaProcess(string? Exe, bool? Elevated);

[Flags]
internal enum UiaPatterns
{
    None = 0,
    Invoke = 1,
    Toggle = 2,
    ExpandCollapse = 4,
    Value = 8,
    SelectionItem = 16,
}

/// <summary>
/// One control as UI Automation reported it. Name is the other app's text, not cleaned yet. A field's value is not
/// read: an editor's value is its whole document, which would be copied across and sent for a list.
/// </summary>
internal sealed record UiaElement(
    int[] RuntimeId,
    string AutomationId,
    string Name,
    UIA_CONTROLTYPE_ID ControlType,
    int ProcessId,
    UiaPatterns Patterns,
    bool Enabled = true,
    bool Offscreen = false,
    bool Password = false,
    ToggleState? Toggle = null,
    ExpandCollapseState? Expand = null,
    bool Selected = false,
    bool ReadOnly = false);

/// <summary>The one pattern call an action makes.</summary>
internal enum UiaCall { Invoke, Toggle, Expand, Collapse, SetValue, Select }

/// <summary>
/// Everything Tomte asks of UI Automation and of Windows about other apps' windows. Every call is made on the
/// automation thread (<see cref="UiaWorker"/>), never on the island's or the input thread; the tests use a fake.
/// </summary>
internal interface IUiAutomation : IDisposable
{
    IReadOnlyList<UiaWindow> Windows();

    /// <summary>The window if it still exists, else null.</summary>
    UiaWindow? Window(nint handle);

    UiaProcess Process(int processId);

    /// <summary>The controls under <paramref name="window"/> that have a pattern Tomte uses, up to <paramref name="max"/>, and how many there were.</summary>
    (IReadOnlyList<UiaElement> Elements, int Total) Elements(nint window, int max);

    /// <summary>
    /// Finds <paramref name="expected"/> again under <paramref name="window"/>, hands what it found now to
    /// <paramref name="check"/>, and makes the call only when the check returns null. Returns null when the call was
    /// made, otherwise why not.
    /// </summary>
    string? Act(nint window, UiaElement expected, UiaCall call, string? text, Func<UiaElement, string?> check);
}
