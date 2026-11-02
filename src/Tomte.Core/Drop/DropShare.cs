using System.Net;

namespace Tomte.Core.Drop;

/// <summary>A file or folder a Tomte Drop offers: the name the phone sees, and where it is on this PC.</summary>
public sealed record DropFile(string Name, string Path);

public enum DropActivityKind
{
    /// <summary>A device opened the page for the first time in this share.</summary>
    Opened,

    /// <summary>A download started: <see cref="DropActivity.Name"/>.</summary>
    Sending,

    /// <summary>A download finished whole: <see cref="DropActivity.Name"/>.</summary>
    Sent,

    /// <summary>An upload started: <see cref="DropActivity.Name"/>.</summary>
    Receiving,

    /// <summary>An upload was saved whole: <see cref="DropActivity.Name"/> at <see cref="DropActivity.Path"/>.</summary>
    Received,

    /// <summary>Text or a link was sent from the page: <see cref="DropActivity.Text"/>.</summary>
    Text,

    /// <summary>A transfer broke off or was refused: <see cref="DropActivity.Name"/>, and <see cref="DropActivity.Text"/> says why.</summary>
    Failed,

    /// <summary>The share ended by itself: nothing was asked of it for <see cref="DropOptions.Idle"/>.</summary>
    Stopped,
}

/// <param name="Device">What the page's browser runs on, as the card names it: "iPhone", "Android phone", "Mac", or "A device".</param>
public sealed record DropActivity(DropActivityKind Kind, string Device, string? Name = null, string? Path = null, string? Text = null);

/// <param name="ReceiveFolder">Where files sent from the page are saved; made when the first one comes.</param>
/// <param name="PcName">What the page calls this PC.</param>
/// <param name="Accent">The island's accent, 0xRRGGBB, which the page takes as its own.</param>
public sealed record DropOptions(string ReceiveFolder, string PcName, uint Accent)
{
    /// <summary>How long a share waits with nothing asked of it, and no transfer running, before it stops by itself.</summary>
    public TimeSpan Idle { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The address to listen on; null picks this PC's address on the local network. Tests give loopback.</summary>
    public IPAddress? Address { get; init; }

    /// <summary>Downloads\Tomte Drop, wherever the user keeps Downloads.</summary>
    public static string DefaultReceiveFolder => throw new NotImplementedException();
}

/// <summary>
/// One Tomte Drop: a small web server on this PC's local network address that a phone reaches through the QR code of
/// <see cref="Link"/>. Its page lists <see cref="Files"/> to download and takes files and text sent back. Every request
/// must carry the share's random token in its path; anything else gets a bare 404. It listens from <see cref="Start"/>
/// until <see cref="Dispose"/> or <see cref="DropOptions.Idle"/>, and never otherwise.
/// </summary>
public sealed class DropShare : IDisposable
{
    /// <summary>The ports a share tries in turn; the Store package's firewall rule opens these on private networks.</summary>
    public const int FirstPort = 53320, LastPort = 53329;

    private DropShare() => throw new NotImplementedException();

    /// <summary>The page's address, token included: what the QR code holds.</summary>
    public Uri Link => throw new NotImplementedException();

    /// <summary>Windows has the network this address is on set to Public, so its firewall keeps phones out.</summary>
    public bool PublicNetwork => throw new NotImplementedException();

    /// <summary>The QR code of <see cref="Link"/>: [row, column], true for a dark module, with no quiet zone.</summary>
    public bool[,] Qr => throw new NotImplementedException();

    public IReadOnlyList<DropFile> Files => throw new NotImplementedException();

    /// <summary>Raised on a pool thread.</summary>
    public event Action<DropActivity>? Activity
    {
        add => throw new NotImplementedException();
        remove => throw new NotImplementedException();
    }

    /// <summary>
    /// Starts listening, or null with <paramref name="problem"/> saying why in a sentence for the card: the PC is on no
    /// local network, or every port is taken. Reads the network's category: call it on a worker thread.
    /// </summary>
    public static DropShare? Start(IReadOnlyList<DropFile> files, DropOptions options, out string? problem, Action<string>? log = null) =>
        throw new NotImplementedException();

    /// <summary>Stops listening and ends every transfer; the link stops working. Safe to call twice.</summary>
    public void Dispose() => throw new NotImplementedException();
}
