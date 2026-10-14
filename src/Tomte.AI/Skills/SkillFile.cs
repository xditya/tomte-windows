using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Tomte.AI.Mcp;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace Tomte.AI.Skills;

/// <summary>
/// Reads one file inside one skill's folder. A skill is text someone else wrote, so the path it or the
/// model names is not trusted: the file is opened first and its real location, with every link and
/// junction on the way resolved, must lie inside the skill's own folder under the skills folder. What
/// is read is what was checked, so swapping a folder for a link after the check changes nothing.
/// </summary>
internal static class SkillFile
{
    /// <summary>
    /// The text of <paramref name="relative"/> inside the skill's folder, cleaned like every outside text
    /// (control characters and the characters that reorder text removed, line breaks kept) and cut at
    /// <paramref name="max"/> characters; Longer says it was cut. Raw is the bytes read: every byte the
    /// text can come from, so a change to what can reach the model changes them. Error says why nothing
    /// was read; it names the path as the caller gave it, never the folder on disk.
    /// </summary>
    public static (string Text, bool Longer, byte[] Raw, string? Error) Read(string skillsFolder, string skill, string relative, int max)
    {
        string shown = McpTools.Clean(relative.Replace('\\', '/'), 200);
        string folder = Path.GetFullPath(Path.Combine(skillsFolder, skill)) + Path.DirectorySeparatorChar;
        // A rooted path, a drive, a stream name (file:stream), a control character or ".." out of the folder.
        bool plain = !relative.Any(char.IsControl) && !Path.IsPathRooted(relative) && !relative.Contains(':');
        string path = plain ? Path.GetFullPath(Path.Combine(folder, relative.Replace('/', '\\'))) : "";
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            return ("", false, [], $"{shown} is not a path inside the {skill} skill's folder. Give one like SKILL.md or references/guide.md.");

        SafeFileHandle handle;
        try
        {
            // Others may keep the file open, even for writing: the check below is on this handle, and so is the read.
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return ("", false, [], $"There is no file {shown} in the {skill} skill's folder.");
        }
        catch (UnauthorizedAccessException)
        {
            return ("", false, [], Directory.Exists(path) ? $"{shown} is a folder, not a file." : $"Windows did not allow reading {shown}.");
        }
        catch (IOException)
        {
            // The exception's text holds the whole path, the Windows user name in it; the model gets the short name only.
            return ("", false, [], $"Could not read {shown}; another program may be holding it.");
        }

        using (handle)
        {
            string inside = FinalPath(skillsFolder) + Path.DirectorySeparatorChar + skill + Path.DirectorySeparatorChar;
            if (!FinalPath(handle).StartsWith(inside, StringComparison.OrdinalIgnoreCase))
                return ("", false, [], $"{shown} leads outside the {skill} skill's folder through a link or a junction, so Tomte did not read it.");
            // A hard link is the same file under a second name, which can be anywhere on the drive.
            if (!PInvoke.GetFileInformationByHandle(handle, out var info))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (info.nNumberOfLinks > 1)
                return ("", false, [], $"{shown} also exists under another name elsewhere on the drive (a hard link), so Tomte did not read it.");

            // At most four bytes per character (UTF-32) and a byte order mark: enough for max + 1 characters in any encoding.
            var raw = new byte[(4 * (max + 1)) + 4];
            int bytes = RandomAccess.Read(handle, raw, 0);
            raw = raw[..bytes];
            using var reader = new StreamReader(new MemoryStream(raw), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[max + 1];
            int read = reader.ReadBlock(buffer);
            var text = new string(buffer, 0, Math.Min(read, max));
            if (text.Contains('\0'))
                return ("", false, raw, $"{shown} is not a text file.");
            return (McpTools.Bound(text, max, lines: true).Text, read > max || RandomAccess.GetLength(handle) > bytes, raw, null);
        }
    }

    /// <summary>
    /// What a skill's "on" switch is stored under: its name, #, and the first 8 bytes of SHA-256 over the bytes of
    /// SKILL.md and of tomte.json (each with its length in front, so the two cannot run into each other; an
    /// absent tomte.json counts as none). Other files in the folder are not covered.
    /// </summary>
    public static string Key(string skill, byte[] skillMd, byte[]? manifest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in (byte[]?[])[skillMd, manifest])
        {
            hash.AppendData(BitConverter.GetBytes(part?.LongLength ?? -1));
            hash.AppendData(part ?? []);
        }

        return skill + "#" + Convert.ToHexStringLower(hash.GetHashAndReset(), 0, 8);
    }

    /// <summary>The line that ends a text cut at <paramref name="max"/> characters. It names Tomte, so a model does not take it for the file.</summary>
    public static string CutNote(string relative, int max) =>
        $"[Cut by Tomte: {McpTools.Clean(relative.Replace('\\', '/'), 200)} is longer; only its first {max} characters were passed on]";

    /// <summary>
    /// Whether <paramref name="path"/>, a file or a folder, is inside <paramref name="folder"/>, by its text or
    /// once every link and junction on the way is followed. A path that does not exist is judged by its text.
    /// </summary>
    public static bool IsInside(string folder, string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return true;
        return Directory.Exists(folder) && Path.Exists(full)
            && FinalPath(full).StartsWith(FinalPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // A file or a folder as the file system resolves it. A folder only opens as a handle with backup semantics.
    private static string FinalPath(string path)
    {
        using var handle = PInvoke.CreateFile(path, (uint)FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE, null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS, null);
        if (handle.IsInvalid)
            throw new IOException($"Could not open {path}.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return FinalPath(handle);
    }

    // The path of an open file with every link and junction followed.
    private static string FinalPath(SafeFileHandle handle)
    {
        Span<char> buffer = stackalloc char[512];
        uint length = PInvoke.GetFinalPathNameByHandle(handle, buffer, 0);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = PInvoke.GetFinalPathNameByHandle(handle, buffer, 0);
        }

        if (length == 0 || length > buffer.Length)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return buffer[..(int)length].ToString();
    }
}
