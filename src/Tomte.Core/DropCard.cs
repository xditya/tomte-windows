using Tomte.Core.Drop;

namespace Tomte.Core;

/// <summary>
/// A Tomte Drop's card, as a <see cref="DropResult"/>: the head says what is offered and that it reaches only this
/// Wi-Fi, the line under it what the phone does now, the QR code with the link and what is offered beside it, the
/// warning, and the files that came from the phone as tiles. Stop sharing is the last choice and the default, with
/// Network settings before it while Windows has the network set to Public. Plain words, no dashes.
/// </summary>
public static class DropCard
{
    public const string Name = "Tomte Drop", Indicator = "Same Wi-Fi only", NotSharing = "Not sharing";
    public const string Stop = "Stop sharing", NetworkSettings = "Network settings";
    public const string Starting = "Starting", Waiting = "Waiting for a phone to scan the code";

    /// <summary>The most files from the phone the card shows as tiles, the newest: one row at the palette's width. All of them are on the shelf.</summary>
    public const int MostTiles = 2;

    /// <summary>Why nothing is shared, for the card's problem line.</summary>
    public const string NotOnShelf = "It could not be put on the shelf, so nothing is shared.";

    public const string SetGone = "None of the set's files are where they were, so nothing is shared.";
    public const string CouldNotStart = "Tomte Drop could not start. Nothing is shared.";

    /// <summary>
    /// Shown only when the network is Public and the firewall has no rule letting phones reach Tomte there. The card
    /// stays up while the user changes it in Settings, and the warning goes once a phone gets through.
    /// </summary>
    public const string PublicNetwork = "Windows has this network set to Public, and its firewall blocks phones here. Set it to Private in Network settings, then scan the code.";

    /// <summary>The card while the share starts.</summary>
    public static DropResult Opening => new(Name, Indicator, Starting, false, "", [], [Stop]);

    /// <summary>The card once the share listens: the code, the link and what is offered beside it, and the warning.</summary>
    public static DropResult Sharing(int count, bool folders, long bytes, Uri link, bool[,] qr, bool publicNetwork) =>
        new(Title(count, folders), Indicator, Waiting, false, SameWifi(count > 0), [], publicNetwork ? [NetworkSettings, Stop] : [Stop])
        {
            Qr = qr,
            Values = count == 0 ? [link.AbsoluteUri] : [link.AbsoluteUri, Summary(count, folders, bytes)],
            Warning = publicNetwork ? PublicNetwork : null,
        };

    /// <summary>The card once a phone opened the page: the network let it through, so the Public warning and its button go.</summary>
    public static DropResult Reached(DropResult card) => card with { Warning = null, Choices = [.. card.Choices.Where(c => c != NetworkSettings)] };

    /// <summary>
    /// The event pill for what happened while the card was closed: what the phone sent, a failure, and the share
    /// stopping by itself, short enough for the pill with the file's name last. Null for the rest, which only the card's
    /// line shows.
    /// </summary>
    public static string? Pill(DropActivity activity) => activity.Kind switch
    {
        DropActivityKind.Received => $"{activity.Device} sent {activity.Name ?? "a file"}",
        DropActivityKind.Text => $"{activity.Device} sent {(IsLink(activity.Text) ? "a link" : "text")}",
        DropActivityKind.Failed => $"Tomte Drop: {activity.Name ?? "a transfer"} failed",
        DropActivityKind.Stopped => "Tomte Drop stopped",
        _ => null,
    };

    /// <summary>The card once the share stopped by itself: the link is dead, so the code and the warning go; the files that came stay.</summary>
    public static DropResult Stopped(DropResult card, TimeSpan idle)
    {
        return card with
        {
            Indicator = NotSharing,
            Line = $"Stopped: nothing was asked for {Minutes(idle)}",
            Body = "",
            Choices = [DropResult.Close],
            Qr = null,
            Values = [],
            Warning = null,
        };
    }

    /// <summary>The card when no share runs, saying why in <paramref name="sentence"/>, with Close.</summary>
    public static DropResult Problem(string sentence) => new DropResult(Name, NotSharing, sentence, true, "", [], []).Closable();

    /// <summary>"Tomte Drop · 3 files", "· 2 items" with a folder among them, or "· receive only" with nothing offered.</summary>
    public static string Title(int count, bool folders) => count == 0 ? $"{Name} · receive only" : $"{Name} · {Count(count, folders)}";

    /// <summary>What is offered and how large its files are, "3 files · 24.5 MB"; a folder's size is not counted.</summary>
    public static string Summary(int count, bool folders, long bytes) => $"{Count(count, folders)} · {ShelfLayout.Size(bytes)}";

    /// <summary>The warning: who can reach the share, and where what the phone sends goes.</summary>
    public static string SameWifi(bool offering) =>
        "Your phone has to be on the same Wi-Fi as this PC. "
        + (offering ? "Anyone on it who has this code can download these files and send files here until you stop. " : "Anyone on it who has this code can send files here until you stop. ")
        + @"Files from the phone go to Downloads\Tomte Drop and onto the shelf.";

    /// <summary>A tile's line for a file the phone sent.</summary>
    public static string From(string device) => $"From {device} · on the shelf";

    /// <summary>
    /// The line under the head for what the phone did: "iPhone opened the page", "Sending report.pdf to iPhone", "Got
    /// IMG_2041.jpg", a failure as the share words it. Null for the share stopping, which changes the whole card.
    /// </summary>
    public static string? Line(DropActivity activity)
    {
        string name = activity.Name ?? "a file", device = Inside(activity.Device);
        return activity.Kind switch
        {
            DropActivityKind.Opened => $"{activity.Device} opened the page",
            DropActivityKind.Sending => $"Sending {name} to {device}",
            DropActivityKind.Sent => $"Sent {name}",
            DropActivityKind.Receiving => $"Getting {name} from {device}",
            DropActivityKind.Received => $"Got {name}",
            DropActivityKind.Text => $"Got {(IsLink(activity.Text) ? "a link" : "text")} from {device} · on the shelf",
            DropActivityKind.Failed => activity.Text ?? $"{activity.Name ?? "A transfer"} did not go through",
            _ => null,
        };
    }

    // "A device" reads "a device" inside a sentence; the names of phones keep their capitals.
    private static string Inside(string device) => device.StartsWith("A ", StringComparison.Ordinal) ? "a" + device[1..] : device;

    // The shelf keeps a lone web address as a link, the rest as text.
    private static bool IsLink(string? text) => text is not null && ShelfFormats.FromText(text).Name.EndsWith(".url", StringComparison.OrdinalIgnoreCase);

    private static string Minutes(TimeSpan idle)
    {
        int minutes = (int)Math.Round(idle.TotalMinutes);
        return $"{minutes} minute{(minutes == 1 ? "" : "s")}";
    }

    private static string Count(int count, bool folders) =>
        FormattableString.Invariant($"{count} {(folders ? "item" : "file")}{(count == 1 ? "" : "s")}");
}
