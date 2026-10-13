namespace Tomte.Core.Device;

// Permission tiers, lowest to highest. The caller decides what to confirm from the tier.
public enum ActionTier
{
    Read,
    AppLocalWrite,
    ExternalEffect,
    Destructive,
}

// Outcome of one device action. Undo is one-shot: the second call reports there is nothing left.
public sealed class ActionResult
{
    private Func<ActionResult>? _undo;

    private ActionResult(bool ok, string message)
    {
        Ok = ok;
        Message = message;
    }

    public bool Ok { get; }
    public string Message { get; }
    public bool UndoAvailable => _undo is not null;

    public ActionResult Undo() =>
        Interlocked.Exchange(ref _undo, null) is { } undo ? undo() : Fail("Nothing to undo.");

    public static ActionResult Done(string message, Func<ActionResult>? undo = null) =>
        new(true, message) { _undo = undo };

    public static ActionResult Fail(string message) => new(false, message);

    public static ActionResult DryRun(string message) => new(true, "Dry run: would " + message);
}

// One entry per action, keyed "Group.Method". The tool list the AI layer exposes is this table.
public static class DeviceActionTiers
{
    public static readonly IReadOnlyDictionary<string, ActionTier> All = new Dictionary<string, ActionTier>
    {
        [$"{nameof(Display)}.{nameof(Display.SetDarkMode)}"] = ActionTier.ExternalEffect,
        [$"{nameof(Display)}.{nameof(Display.SetNightLight)}"] = ActionTier.ExternalEffect,
        [$"{nameof(Display)}.{nameof(Display.SetBrightness)}"] = ActionTier.ExternalEffect,
        [$"{nameof(Radios)}.{nameof(Radios.SetWifi)}"] = ActionTier.ExternalEffect,
        [$"{nameof(Radios)}.{nameof(Radios.SetBluetooth)}"] = ActionTier.ExternalEffect,
        [$"{nameof(Audio)}.{nameof(Audio.SetVolume)}"] = ActionTier.ExternalEffect,
        [$"{nameof(Audio)}.{nameof(Audio.SetMute)}"] = ActionTier.ExternalEffect,
        [$"{nameof(Audio)}.{nameof(Audio.SetMicMute)}"] = ActionTier.ExternalEffect,
        [$"{nameof(Audio)}.{nameof(Audio.ListOutputDevices)}"] = ActionTier.Read,
        [$"{nameof(Audio)}.{nameof(Audio.SetOutputDevice)}"] = ActionTier.ExternalEffect,
        [$"{nameof(Apps)}.{nameof(Apps.OpenApp)}"] = ActionTier.ExternalEffect,
        [$"{nameof(Apps)}.{nameof(Apps.OpenSettingsPage)}"] = ActionTier.ExternalEffect,
        [$"{nameof(Power)}.{nameof(Power.Lock)}"] = ActionTier.Destructive,
        [$"{nameof(Power)}.{nameof(Power.Sleep)}"] = ActionTier.Destructive,
        [$"{nameof(Power)}.{nameof(Power.Restart)}"] = ActionTier.Destructive,
        [$"{nameof(Power)}.{nameof(Power.ShutDown)}"] = ActionTier.Destructive,
    };
}
