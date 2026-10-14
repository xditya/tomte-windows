namespace Tomte.Core;

/// <summary>
/// Paths that lead off this PC. Both checks read only the text and the drive's type, so telling such a path
/// apart never connects to a server, which would send the user's credentials before anyone was asked.
/// </summary>
public static class NetworkPaths
{
    /// <summary>Two leading separators of either kind start a network or device path: Windows reads every mix of them as two backslashes.</summary>
    public static bool IsUncLike(string path) => path is ['\\' or '/', '\\' or '/', ..];

    /// <summary>A full path whose drive letter is mapped to a network share.</summary>
    public static bool OnNetworkDrive(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
