using System.Numerics;
using System.Runtime.InteropServices;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Triggers;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// The microphone during calls. While another app has the microphone open (<see cref="MicUseWatch"/>) and the setting is
/// on, the push pill and the pill kept while music plays grow a round mic button at their trailing end; a click mutes or
/// unmutes the default communications microphone, and while it is muted the button shows a struck mic in the warning
/// colour. The mute key does the same whenever there is a microphone, call or not, and shows the new state on a pill of
/// its own when no button is up. The island takes no focus, so the call app keeps it. Nothing runs between changes: the
/// watch and the mute both wait on Core Audio callbacks, and the microphone is held only during a call.
/// </summary>
internal sealed class CallMute
{
    private readonly IslandWindow island;
    private readonly TriggerController triggers;
    private readonly Action<string>? log;
    private MicUseWatch? watch;
    private CommsMic? mic;
    private Button? button;
    private bool listening, call, muted;

    public CallMute(IslandWindow island, TriggerController triggers, SettingsStore store, Action<string>? log)
    {
        this.island = island;
        this.triggers = triggers;
        this.log = log;
        triggers.MicClicked = Toggle;
        triggers.MuteKey = Toggle;
        // Why the pill is up is settled only after the change that raised these, so they are read on the next turn. A
        // pill that goes takes its button at once, so the button fades with the rest of the content.
        triggers.CompactChanged += () => island.Queue.TryEnqueue(Update);
        triggers.Machine.Changed += (_, to) =>
        {
            if (to == IslandState.Compact)
            {
                island.Queue.TryEnqueue(Update);
            }
            else
            {
                Update();
            }
        };
        island.ContentReady += () =>
        {
            if (triggers.State == IslandState.Compact)
            {
                button?.FadeIn();
            }
        };
        Apply(store.Current);
        // Changed arrives on a pool thread.
        store.Changed += s => island.Queue.TryEnqueue(() => Apply(s));
    }

    private void Apply(TomteSettings settings)
    {
        if (settings.MuteDuringCalls == listening)
        {
            return;
        }

        listening = settings.MuteDuringCalls;
        watch?.Dispose();
        watch = listening ? MicUseWatch.Start() : null;
        if (watch is { } w)
        {
            w.Changed += now => island.Queue.TryEnqueue(() =>
            {
                if (watch == w)
                {
                    OnCall(now);
                }
            });
        }

        OnCall(watch?.Capturing == true);
    }

    private void OnCall(bool now)
    {
        // Opened again on every change: an app starting or stopping the microphone is when the default device changes.
        mic?.Dispose();
        mic = now ? Open() : null;
        if (now != call)
        {
            log?.Invoke($"mic: {(now ? "another app has the microphone open" : "no other app has the microphone open")}{(now && mic is null ? ", but there is no communications microphone" : "")}");
        }

        call = now;
        Update();
    }

    /// <summary>The default communications microphone, its mute read and followed, or null when there is none.</summary>
    private CommsMic? Open()
    {
        var opened = CommsMic.Open();
        if (opened is not null)
        {
            muted = opened.Muted;
            opened.MuteChanged += value => island.Queue.TryEnqueue(() =>
            {
                if (mic == opened)
                {
                    muted = value;
                    Update();
                }
            });
        }

        return opened;
    }

    /// <summary>The button or the mute key: the mute the other way, on the microphone that is the default now.</summary>
    private void Toggle()
    {
        mic?.Dispose();
        mic = Open();
        if (mic is null)
        {
            log?.Invoke("mic: no communications microphone to mute");
            return;
        }

        try
        {
            mic.SetMuted(!muted);
            muted = !muted;
        }
        catch (COMException e)
        {
            log?.Invoke($"mic: mute failed, 0x{e.HResult:X8}");
        }

        // The key's own pill shows the state where no button does, and says the new one if it is up.
        if (!triggers.MicShown || triggers.EventOwner == IslandEventSource.Mic)
        {
            triggers.FlashMic(muted);
        }

        if (!call)
        {
            mic.Dispose();
            mic = null;
        }

        Update();
    }

    private void Update()
    {
        bool on = triggers.State == IslandState.Compact
            && CompactRules.ShowsMic(triggers.Compact, triggers.EventOwner, call && mic is not null, triggers.PeekShowing);
        triggers.ShowMic(on, CompactLayout.MicTip(muted));
        if (triggers.MicShown)
        {
            button ??= new Button(island);
            button.Draw(muted);
        }
        else if (button is not null)
        {
            button.Remove();
            button = null;
        }
    }

    /// <summary>The round button in island space, at <see cref="CompactLayout.MicButton"/>. Its hover and press come from the interaction host.</summary>
    private sealed class Button
    {
        private readonly IslandWindow island;
        private readonly ShapeVisual shapes;
        private readonly CompositionSpriteShape disc;
        private readonly CompositionContainerShape glyph;
        private bool fadeInPending = true;

        public Button(IslandWindow island)
        {
            this.island = island;
            var c = island.Compositor;
            var box = CompactLayout.MicButton;
            shapes = c.CreateShapeVisual();
            shapes.Offset = new Vector3(box.X, box.Y, 0);
            shapes.Size = new Vector2(box.Width, box.Height);
            shapes.Opacity = 0;
            var circle = c.CreateEllipseGeometry();
            circle.Center = circle.Radius = new Vector2(box.Width / 2);
            disc = c.CreateSpriteShape(circle);
            shapes.Shapes.Add(disc);
            glyph = c.CreateContainerShape();
            glyph.Offset = new Vector2((box.Width - CompactLayout.MicGlyph) / 2);
            shapes.Shapes.Add(glyph);
            island.Dips.Children.InsertAtTop(shapes);
        }

        /// <summary>A quiet raised disc with the mic, or the warning colour's soft tint with the mic struck through.</summary>
        public void Draw(bool muted)
        {
            var c = island.Compositor;
            disc.FillBrush = c.CreateColorBrush(ShelfColors.Of(muted ? Theme.WarnSoft : Theme.Raised));
            PaletteView.DrawGlyph(glyph, muted ? Glyphs.MicOff : Glyphs.Mic, c.CreateColorBrush(ShelfColors.Of(muted ? Theme.Warn : Theme.Ink)), CompactLayout.MicGlyph);
        }

        /// <summary>In with the rest of the pill's content, once the shape is most of the way there.</summary>
        public void FadeIn()
        {
            if (fadeInPending)
            {
                fadeInPending = false;
                shapes.StartAnimation("Opacity", PanelHost.Fade(island.Compositor, 1, PanelHost.FadeInMs));
            }
        }

        /// <summary>Fades out over 0.1 s, then goes: on a timer, since a compositor that is not presenting completes no animation.</summary>
        public void Remove()
        {
            shapes.StartAnimation("Opacity", PanelHost.Fade(island.Compositor, 0, PanelHost.FadeOutMs));
            PanelHost.AfterFadeOut(island, () =>
            {
                island.Dips.Children.Remove(shapes);
                shapes.Dispose();
            }).Start();
        }
    }
}
