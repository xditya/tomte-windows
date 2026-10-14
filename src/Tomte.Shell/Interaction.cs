using System.Numerics;
using Tomte.Core.Interaction;
using Windows.Win32.Foundation;

namespace Tomte.Shell;

/// <summary>
/// A view whose clickable parts the island's <see cref="InteractionHost"/> runs: hover, press and release, the cursor,
/// tooltips and keyboard focus. The view says where its targets are and what a click on one does, and draws no hover or
/// press look of its own.
/// </summary>
internal interface IInteractive
{
    /// <summary>The targets in Tab order, each in the view's own DIP space.</summary>
    IReadOnlyList<Target> Targets { get; }

    /// <summary>Raised when the list changes, or a target's bounds, <see cref="Target.Enabled"/> or <see cref="Target.Checked"/> do.</summary>
    event Action? TargetsChanged;

    /// <summary>A click released on the target, or Enter or Space while it has keyboard focus.</summary>
    void Activate(string id);

    /// <summary>
    /// The same, saying how: the host calls this one. A view where a click, a double click and Enter mean different
    /// things (a shelf tile: select, open, open) answers it; any other leaves it to <see cref="Activate(string)"/>.
    /// </summary>
    void Activate(string id, ActivateBy by) => Activate(id);

    /// <summary>
    /// A press on a <see cref="Target.Draggable"/> target, at this point of the view's own space. Runs DragDetect and the
    /// drag itself and returns true if a drag happened; false means the button came up without one, and the host takes
    /// it as a click.
    /// </summary>
    bool TryDrag(string id, Vector2 at) => false;

    /// <summary>
    /// The pointer moved onto another of this view's targets, or keyboard focus did; null when it left them all. For
    /// content that shows on hover, such as a tile's action row; the host draws the state layer itself.
    /// </summary>
    void Hovered(string? id) { }

    /// <summary>
    /// A key while the island has the keys and one of this view's targets has focus, before the host's own Tab, arrow,
    /// Enter and Space handling. True when the view took it: Delete on a row, Left and Right on a seek bar.
    /// </summary>
    bool KeyDown(string id, int virtualKey) => false;

    /// <summary>
    /// The pointer moved over one of this view's targets, at this point of the view's own space. Not called during a
    /// press the host holds or a <see cref="TryDrag"/> loop. For content that follows the pointer, such as the time
    /// under it on a seek bar.
    /// </summary>
    void PointerMoved(string id, Vector2 at) { }
}

/// <summary>
/// The island's one owner of pointer and keyboard interaction with registered views. A view registers with the functions
/// from island space to its own space and back; it unregisters when it hides. Island thread only.
/// </summary>
internal sealed class InteractionHost
{
    private readonly List<View> views = [];

    /// <summary>Starts running <paramref name="view"/>'s targets; registering it again replaces its functions.</summary>
    public void Register(IInteractive view, Func<Vector2, Vector2> toView, Func<Vector2, Vector2> fromView)
    {
        Unregister(view);
        views.Add(new View(view, toView, fromView));
    }

    /// <summary>The view hid: its targets are nobody's now. A view that is not registered is ignored.</summary>
    public void Unregister(IInteractive view) => views.RemoveAll(v => ReferenceEquals(v.Interactive, view));

    /// <summary>
    /// A short confirmation at one of the view's targets, after an action that shows nothing by itself (a copy): a tick
    /// and <paramref name="text"/> on the tooltip's card for <see cref="StateLook.AcknowledgeMs"/>, in place of any tooltip.
    /// </summary>
    public void Acknowledge(IInteractive view, string id, string text = "Copied")
    {
    }

    /// <summary>
    /// The island's pointer, cursor and key messages, before the old handlers: the answer, or null when no registered
    /// target took the message and the old handlers should have it.
    /// </summary>
    internal LRESULT? OnMessage(uint message, nuint wParam, nint lParam) => null;

    private sealed record View(IInteractive Interactive, Func<Vector2, Vector2> ToView, Func<Vector2, Vector2> FromView);
}
