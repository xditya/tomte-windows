using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Accessibility;

namespace Tomte.AI.Automation;

/// <summary>
/// <see cref="IUiAutomation"/> through the UI Automation client COM API. Made on the automation thread by the first
/// call and used only there. Every element, array, pattern and condition is released as soon as it has been read;
/// nothing subscribes to UI Automation events.
/// </summary>
internal sealed unsafe class UiaClient : IUiAutomation
{
    private const uint HighIntegrity = 0x3000;

    private static readonly UIA_PROPERTY_ID[] Cached =
    [
        UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId, UIA_PROPERTY_ID.UIA_AutomationIdPropertyId, UIA_PROPERTY_ID.UIA_NamePropertyId,
        UIA_PROPERTY_ID.UIA_ControlTypePropertyId, UIA_PROPERTY_ID.UIA_ProcessIdPropertyId, UIA_PROPERTY_ID.UIA_IsEnabledPropertyId,
        UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId, UIA_PROPERTY_ID.UIA_IsPasswordPropertyId,
        UIA_PROPERTY_ID.UIA_IsInvokePatternAvailablePropertyId, UIA_PROPERTY_ID.UIA_IsTogglePatternAvailablePropertyId,
        UIA_PROPERTY_ID.UIA_IsExpandCollapsePatternAvailablePropertyId, UIA_PROPERTY_ID.UIA_IsValuePatternAvailablePropertyId,
        UIA_PROPERTY_ID.UIA_IsSelectionItemPatternAvailablePropertyId, UIA_PROPERTY_ID.UIA_ToggleToggleStatePropertyId,
        UIA_PROPERTY_ID.UIA_ExpandCollapseExpandCollapseStatePropertyId, UIA_PROPERTY_ID.UIA_SelectionItemIsSelectedPropertyId,
        UIA_PROPERTY_ID.UIA_ValueIsReadOnlyPropertyId,
    ];

    private static readonly (UIA_PROPERTY_ID Property, UiaPatterns Pattern)[] Patterns =
    [
        (UIA_PROPERTY_ID.UIA_IsInvokePatternAvailablePropertyId, UiaPatterns.Invoke),
        (UIA_PROPERTY_ID.UIA_IsTogglePatternAvailablePropertyId, UiaPatterns.Toggle),
        (UIA_PROPERTY_ID.UIA_IsExpandCollapsePatternAvailablePropertyId, UiaPatterns.ExpandCollapse),
        (UIA_PROPERTY_ID.UIA_IsValuePatternAvailablePropertyId, UiaPatterns.Value),
        (UIA_PROPERTY_ID.UIA_IsSelectionItemPatternAvailablePropertyId, UiaPatterns.SelectionItem),
    ];

    private readonly IUIAutomation2 _uia;
    private readonly IUIAutomationCacheRequest _cache;

    // Control view, and at least one of the patterns the tools use.
    private readonly IUIAutomationCondition _actionable;

    public UiaClient()
    {
        _uia = (IUIAutomation2)new CUIAutomation8();
        // A hung app would hold the automation thread for the default 20 s; UI Automation gives up first.
        _uia.ConnectionTimeout = 2000;
        _uia.TransactionTimeout = 4000;
        _cache = _uia.CreateCacheRequest();
        foreach (var property in Cached)
            _cache.AddProperty(property);

        IUIAutomationCondition? any = null;
        foreach (var (property, _) in Patterns)
        {
            var one = _uia.CreatePropertyCondition(property, true);
            if (any is null)
            {
                any = one;
                continue;
            }

            var both = _uia.CreateOrCondition(any, one);
            Release(any);
            Release(one);
            any = both;
        }

        var view = _uia.ControlViewCondition;
        _actionable = _uia.CreateAndCondition(view, any!);
        Release(view);
        Release(any);
    }

    public IReadOnlyList<UiaWindow> Windows()
    {
        var windows = new List<UiaWindow>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (Window(hwnd) is { Title.Length: > 0 } window && PInvoke.IsWindowVisible(hwnd) && !Cloaked(hwnd))
                windows.Add(window);
            return true;
        }, 0);
        return windows;
    }

    public UiaWindow? Window(nint handle)
    {
        var hwnd = new HWND(handle);
        if (!PInvoke.IsWindow(hwnd))
            return null;
        uint pid;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        Span<char> title = stackalloc char[256];
        return new(handle, (int)pid, new string(title[..PInvoke.GetWindowText(hwnd, title)]));
    }

    public UiaProcess Process(int processId)
    {
        using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process.IsInvalid)
            return new(null, null);
        Span<char> path = stackalloc char[1024];
        uint length = (uint)path.Length;
        string? exe = PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, path, ref length)
            ? Path.GetFileName(path[..(int)length].ToString())
            : null;
        return new(exe, Elevated(process));
    }

    public (IReadOnlyList<UiaElement> Elements, int Total) Elements(nint window, int max)
    {
        var root = _uia.ElementFromHandle(new HWND(window));
        try
        {
            var found = root.FindAllBuildCache(TreeScope.TreeScope_Descendants, _actionable, _cache);
            if (found is null)
                return ([], 0);
            try
            {
                int total = found.Length;
                var elements = new List<UiaElement>(Math.Min(total, max));
                for (int i = 0; i < total && elements.Count < max; i++)
                {
                    var element = found.GetElement(i);
                    try
                    {
                        elements.Add(Read(element));
                    }
                    finally
                    {
                        Release(element);
                    }
                }

                return (elements, total);
            }
            finally
            {
                Release(found);
            }
        }
        finally
        {
            Release(root);
        }
    }

    public string? Act(nint window, UiaElement expected, UiaCall call, string? text, Func<UiaElement, string?> check)
    {
        var root = _uia.ElementFromHandle(new HWND(window));
        IUIAutomationElement? element = null;
        object? pattern = null;
        try
        {
            element = Find(root, expected);
            if (element is null)
                return "the control is no longer there. List the window again.";
            if (check(Read(element)) is { } changed)
                return changed;
            pattern = element.GetCurrentPattern(PatternId(call));
            switch (pattern)
            {
                case IUIAutomationInvokePattern invoke when call == UiaCall.Invoke:
                    invoke.Invoke();
                    break;
                case IUIAutomationTogglePattern toggle when call == UiaCall.Toggle:
                    toggle.Toggle();
                    break;
                case IUIAutomationExpandCollapsePattern expand when call == UiaCall.Expand:
                    expand.Expand();
                    break;
                case IUIAutomationExpandCollapsePattern collapse when call == UiaCall.Collapse:
                    collapse.Collapse();
                    break;
                case IUIAutomationValuePattern value when call == UiaCall.SetValue:
                    nint bstr = Marshal.StringToBSTR(text ?? "");
                    try
                    {
                        value.SetValue(new BSTR((char*)bstr));
                    }
                    finally
                    {
                        Marshal.FreeBSTR(bstr);
                    }

                    break;
                case IUIAutomationSelectionItemPattern select when call == UiaCall.Select:
                    select.Select();
                    break;
                default:
                    return "the control no longer offers that action.";
            }

            return null;
        }
        finally
        {
            Release(pattern);
            Release(element);
            Release(root);
        }
    }

    public void Dispose()
    {
        Release(_actionable);
        Release(_cache);
        Release(_uia);
    }

    /// <summary>By runtime id first; when that is gone, by automation id and role, and only if exactly one control matches.</summary>
    private IUIAutomationElement? Find(IUIAutomationElement root, UiaElement expected)
    {
        if (expected.RuntimeId.Length > 0)
        {
            var byRuntimeId = _uia.CreatePropertyCondition(UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId, expected.RuntimeId);
            try
            {
                if (root.FindFirstBuildCache(TreeScope.TreeScope_Descendants, byRuntimeId, _cache) is { } found)
                    return found;
            }
            finally
            {
                Release(byRuntimeId);
            }
        }

        if (expected.AutomationId.Length == 0)
            return null;
        var id = _uia.CreatePropertyCondition(UIA_PROPERTY_ID.UIA_AutomationIdPropertyId, expected.AutomationId);
        var type = _uia.CreatePropertyCondition(UIA_PROPERTY_ID.UIA_ControlTypePropertyId, (int)expected.ControlType);
        var both = _uia.CreateAndCondition(id, type);
        Release(id);
        Release(type);
        IUIAutomationElementArray? all = null;
        try
        {
            all = root.FindAllBuildCache(TreeScope.TreeScope_Descendants, both, _cache);
            return all is { Length: 1 } ? all.GetElement(0) : null;
        }
        finally
        {
            Release(all);
            Release(both);
        }
    }

    private static UiaElement Read(IUIAutomationElement e)
    {
        var patterns = UiaPatterns.None;
        foreach (var (property, pattern) in Patterns)
        {
            if (Get(e, property) is true)
                patterns |= pattern;
        }

        return new(
            Get(e, UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId) as int[] ?? [],
            Get(e, UIA_PROPERTY_ID.UIA_AutomationIdPropertyId) as string ?? "",
            Get(e, UIA_PROPERTY_ID.UIA_NamePropertyId) as string ?? "",
            (UIA_CONTROLTYPE_ID)(Get(e, UIA_PROPERTY_ID.UIA_ControlTypePropertyId) as int? ?? 0),
            Get(e, UIA_PROPERTY_ID.UIA_ProcessIdPropertyId) as int? ?? 0,
            patterns,
            Enabled: Get(e, UIA_PROPERTY_ID.UIA_IsEnabledPropertyId) is true,
            Offscreen: Get(e, UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId) is true,
            Password: Get(e, UIA_PROPERTY_ID.UIA_IsPasswordPropertyId) is true,
            Toggle: patterns.HasFlag(UiaPatterns.Toggle) && Get(e, UIA_PROPERTY_ID.UIA_ToggleToggleStatePropertyId) is int toggle ? (ToggleState)toggle : null,
            Expand: patterns.HasFlag(UiaPatterns.ExpandCollapse) && Get(e, UIA_PROPERTY_ID.UIA_ExpandCollapseExpandCollapseStatePropertyId) is int expand ? (ExpandCollapseState)expand : null,
            Selected: Get(e, UIA_PROPERTY_ID.UIA_SelectionItemIsSelectedPropertyId) is true,
            ReadOnly: Get(e, UIA_PROPERTY_ID.UIA_ValueIsReadOnlyPropertyId) is true);
    }

    /// <summary>A cached property; null for UI Automation's "not supported" value, which arrives as a COM object.</summary>
    private static object? Get(IUIAutomationElement e, UIA_PROPERTY_ID property)
    {
        var value = e.GetCachedPropertyValue(property);
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
            return null;
        }

        return value;
    }

    private static UIA_PATTERN_ID PatternId(UiaCall call) => call switch
    {
        UiaCall.Invoke => UIA_PATTERN_ID.UIA_InvokePatternId,
        UiaCall.Toggle => UIA_PATTERN_ID.UIA_TogglePatternId,
        UiaCall.Expand or UiaCall.Collapse => UIA_PATTERN_ID.UIA_ExpandCollapsePatternId,
        UiaCall.SetValue => UIA_PATTERN_ID.UIA_ValuePatternId,
        _ => UIA_PATTERN_ID.UIA_SelectionItemPatternId,
    };

    /// <summary>
    /// High integrity or above: elevated, or a system process. Null when the token cannot be read, which is what an
    /// elevated process usually answers a normal one.
    /// </summary>
    private static bool? Elevated(SafeHandle process)
    {
        if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
            return null;
        using (token)
        {
            byte* buffer = stackalloc byte[128];
            if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer, 128, out _))
                return null;
            var sid = ((TOKEN_MANDATORY_LABEL*)buffer)->Label.Sid;
            byte count = *PInvoke.GetSidSubAuthorityCount(sid);
            return count == 0 ? null : *PInvoke.GetSidSubAuthority(sid, (uint)count - 1) >= HighIntegrity;
        }
    }

    /// <summary>On another virtual desktop, or a suspended Store app's frame.</summary>
    private static bool Cloaked(HWND hwnd)
    {
        uint cloaked = 0;
        return PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint)).Succeeded && cloaked != 0;
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
            Marshal.ReleaseComObject(com);
    }
}
