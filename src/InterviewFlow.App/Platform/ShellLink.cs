using System.Text;

namespace InterviewFlow.App.Platform;

/// <summary>
/// Windows shell link (<c>.lnk</c>) files, read and written in managed code
/// from the MS-SHLLINK layout. The usual route — <c>WScript.Shell</c> over
/// COM — needs built-in COM interop and the dynamic binder, both of which the
/// trimmed release publish drops (Directory.Build.props), so the format is
/// handled here instead. Pure byte work: it runs on any OS, which is also
/// what makes it testable.
/// </summary>
public static class ShellLink
{
    private const uint HasLinkTargetIdList = 0x1;
    private const uint HasLinkInfo = 0x2;
    private const uint HasName = 0x4;
    private const uint HasRelativePath = 0x8;
    private const uint HasWorkingDir = 0x10;
    private const uint HasArguments = 0x20;
    private const uint HasIconLocation = 0x40;
    private const uint IsUnicode = 0x80;

    private const uint EnvironmentVariableBlock = 0xA0000001;

    private static readonly byte[] LinkClsid =
        [0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];

    /// <summary>
    /// A link to <paramref name="targetPath"/> with no argument list. The
    /// icon is the target's own (index 0) unless another file is named.
    /// </summary>
    public static byte[] Create(
        string targetPath, string workingDirectory, string description = "", string iconPath = "", int iconIndex = 0)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);

        var flags = HasLinkTargetIdList | HasLinkInfo | IsUnicode;
        if (description.Length > 0) flags |= HasName;
        if (workingDirectory.Length > 0) flags |= HasWorkingDir;
        if (iconPath.Length > 0) flags |= HasIconLocation;

        // ShellLinkHeader (0x4C bytes).
        w.Write(0x4Cu);
        w.Write(LinkClsid);
        w.Write(flags);
        w.Write(0x20u);                 // FILE_ATTRIBUTE_ARCHIVE
        w.Write(0L); w.Write(0L); w.Write(0L); // creation / access / write time: unknown
        w.Write(0u);                    // FileSize
        w.Write(iconIndex);
        w.Write(1u);                    // SW_SHOWNORMAL
        w.Write((ushort)0);             // HotKey
        w.Write((ushort)0); w.Write(0u); w.Write(0u); // reserved

        WriteIdList(w, targetPath);
        WriteLinkInfo(w, targetPath);

        if (description.Length > 0) WriteString(w, description);
        if (workingDirectory.Length > 0) WriteString(w, workingDirectory);
        if (iconPath.Length > 0) WriteString(w, iconPath);

        w.Write(0u);                    // ExtraData: terminal block
        w.Flush();
        return stream.ToArray();
    }

    // The shell namespace path to a local file: "My Computer", the drive, then
    // one file-system item per path segment. Windows resolves a link from this
    // list — a link carrying only LinkInfo reads back with an empty target.
    // The item layouts are the ones the shell has written since XP (the
    // 0xBEEF0004 extension holds the long name in Unicode; the item body
    // keeps an 8.3-style name the shell tolerates as the full ANSI name).
    private static void WriteIdList(BinaryWriter w, string targetPath)
    {
        var full = Path.GetFullPath(targetPath);
        var root = Path.GetPathRoot(full) ?? "";
        var segments = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        using var list = new MemoryStream();
        using var lw = new BinaryWriter(list);

        // "My Computer": {20D04FE0-3AEA-1069-A2D8-08002B30309D}.
        lw.Write((ushort)0x14);
        lw.Write((byte)0x1F);
        lw.Write((byte)0x50);
        lw.Write(new byte[] { 0xE0, 0x4F, 0xD0, 0x20, 0xEA, 0x3A, 0x69, 0x10, 0xA2, 0xD8, 0x08, 0x00, 0x2B, 0x30, 0x30, 0x9D });

        // The drive: type 0x2F, "C:\" as ANSI, zero-padded to the fixed size.
        var drive = new byte[0x19];
        drive[0] = 0x19;
        drive[2] = 0x2F;
        Encoding.Latin1.GetBytes(root).CopyTo(drive, 3);
        lw.Write(drive);

        for (var i = 0; i < segments.Length; i++)
            WriteFileSystemItem(lw, segments[i], isFolder: i < segments.Length - 1);

        lw.Write((ushort)0); // TerminalID
        lw.Flush();

        w.Write((ushort)list.Length);
        w.Write(list.ToArray());
    }

    private static void WriteFileSystemItem(BinaryWriter w, string name, bool isFolder)
    {
        var ansi = Encoding.Latin1.GetBytes(name);
        var unicode = Encoding.Unicode.GetBytes(name);

        // Body: type, a reserved byte, file size, DOS date/time, attributes,
        // ANSI name + NUL, padded so the extension block starts on an even
        // offset.
        var unpadded = 1 + 1 + 4 + 4 + 2 + ansi.Length + 1;
        var bodyLength = unpadded % 2 == 0 ? unpadded : unpadded + 1;

        // 0xBEEF0004 (version 3): size, version, signature, creation and
        // access DOS date/time, identifier, long name + NUL, offset of this
        // block's version field from the item's start.
        var extensionLength = 2 + 2 + 4 + 4 + 4 + 2 + unicode.Length + 2 + 2;
        var itemSize = 2 + bodyLength + extensionLength;

        w.Write((ushort)itemSize);
        w.Write((byte)(isFolder ? 0x31 : 0x32));
        w.Write((byte)0);                               // reserved
        w.Write(0u);                                    // file size: unknown
        w.Write(0u);                                    // modified: unknown
        w.Write((ushort)(isFolder ? 0x10 : 0x20));      // DIRECTORY / ARCHIVE
        w.Write(ansi);
        w.Write((byte)0);
        if (bodyLength != unpadded)
            w.Write((byte)0);

        w.Write((ushort)extensionLength);
        w.Write((ushort)3);
        w.Write(0xBEEF0004u);
        w.Write(0u);
        w.Write(0u);
        w.Write((ushort)0x14);
        w.Write(unicode);
        w.Write((ushort)0);
        w.Write((ushort)(2 + bodyLength));              // version-field offset
    }

    /// <summary>
    /// LinkInfo carrying a local volume and base path — what a reader gets
    /// the target from without walking the shell namespace.
    /// </summary>
    private static void WriteLinkInfo(BinaryWriter w, string targetPath)
    {
        var ansiPath = Encoding.Latin1.GetBytes(targetPath);
        var unicodePath = Encoding.Unicode.GetBytes(targetPath);
        var root = Path.GetPathRoot(targetPath) ?? "";
        var ansiRoot = Encoding.Latin1.GetBytes(root);

        const int headerSize = 0x24;             // includes the Unicode offsets
        var volumeIdSize = 0x10 + ansiRoot.Length + 1; // fixed part + label (we write the root) + NUL
        var volumeIdOffset = headerSize;
        var localBasePathOffset = volumeIdOffset + volumeIdSize;
        var commonPathSuffixOffset = localBasePathOffset + ansiPath.Length + 1;
        var localBasePathOffsetUnicode = commonPathSuffixOffset + 1;
        var commonPathSuffixOffsetUnicode = localBasePathOffsetUnicode + unicodePath.Length + 2;
        var linkInfoSize = commonPathSuffixOffsetUnicode + 2;

        w.Write((uint)linkInfoSize);
        w.Write((uint)headerSize);
        w.Write(1u);                             // VolumeIDAndLocalBasePath
        w.Write((uint)volumeIdOffset);
        w.Write((uint)localBasePathOffset);
        w.Write(0u);                             // CommonNetworkRelativeLinkOffset
        w.Write((uint)commonPathSuffixOffset);
        w.Write((uint)localBasePathOffsetUnicode);
        w.Write((uint)commonPathSuffixOffsetUnicode);

        // VolumeID.
        w.Write((uint)volumeIdSize);
        w.Write(3u);                             // DRIVE_FIXED
        w.Write(0u);                             // serial number: unknown
        w.Write(0x10u);                          // VolumeLabelOffset
        w.Write(ansiRoot); w.Write((byte)0);

        w.Write(ansiPath); w.Write((byte)0);     // LocalBasePath
        w.Write((byte)0);                        // CommonPathSuffix (empty)
        w.Write(unicodePath); w.Write((ushort)0); // LocalBasePathUnicode
        w.Write((ushort)0);                      // CommonPathSuffixUnicode (empty)
    }

    private static void WriteString(BinaryWriter w, string value)
    {
        w.Write((ushort)value.Length);
        w.Write(Encoding.Unicode.GetBytes(value));
    }

    /// <summary>
    /// The path a link points at, or null when the file is not a shell link
    /// or names no local path (an advertised-install or network link).
    /// Reads LinkInfo first, then an EnvironmentVariableDataBlock, which is
    /// where a link written with <c>%ProgramFiles%</c> keeps its target.
    /// </summary>
    public static string? ReadTargetPath(byte[] bytes)
    {
        try
        {
            return Parse(bytes);
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentOutOfRangeException
                                   or ArgumentException or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>File form of <see cref="ReadTargetPath(byte[])"/>; unreadable files yield null.</summary>
    public static string? ReadTargetPath(string path)
    {
        try
        {
            return ReadTargetPath(File.ReadAllBytes(path));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Parse(byte[] bytes)
    {
        if (bytes.Length < 0x4C || BitConverter.ToUInt32(bytes, 0) != 0x4C
            || !bytes.AsSpan(4, 16).SequenceEqual(LinkClsid))
        {
            return null;
        }

        var flags = BitConverter.ToUInt32(bytes, 0x14);
        var unicode = (flags & IsUnicode) != 0;
        var pos = 0x4C;

        if ((flags & HasLinkTargetIdList) != 0)
            pos += 2 + BitConverter.ToUInt16(bytes, pos);

        string? target = null;
        if ((flags & HasLinkInfo) != 0)
        {
            var start = pos;
            var size = checked((int)BitConverter.ToUInt32(bytes, start));
            var headerSize = checked((int)BitConverter.ToUInt32(bytes, start + 4));
            var infoFlags = BitConverter.ToUInt32(bytes, start + 8);
            if ((infoFlags & 1) != 0)
            {
                var suffix = "";
                if (headerSize >= 0x24)
                {
                    var baseOffset = checked((int)BitConverter.ToUInt32(bytes, start + 0x1C));
                    var suffixOffset = checked((int)BitConverter.ToUInt32(bytes, start + 0x20));
                    target = ReadUtf16Z(bytes, start + baseOffset);
                    suffix = ReadUtf16Z(bytes, start + suffixOffset);
                }
                else
                {
                    var baseOffset = checked((int)BitConverter.ToUInt32(bytes, start + 0x10));
                    var suffixOffset = checked((int)BitConverter.ToUInt32(bytes, start + 0x18));
                    target = ReadAnsiZ(bytes, start + baseOffset);
                    suffix = ReadAnsiZ(bytes, start + suffixOffset);
                }

                if (suffix.Length > 0)
                    target = Path.Combine(target, suffix);
            }

            pos = start + size;
        }

        if (target is { Length: > 0 })
            return target;

        // StringData: skip whatever is present to reach the extra-data blocks.
        foreach (var flag in new[] { HasName, HasRelativePath, HasWorkingDir, HasArguments, HasIconLocation })
        {
            if ((flags & flag) == 0)
                continue;
            var count = BitConverter.ToUInt16(bytes, pos);
            pos += 2 + count * (unicode ? 2 : 1);
        }

        while (pos + 8 <= bytes.Length)
        {
            var size = checked((int)BitConverter.ToUInt32(bytes, pos));
            if (size < 4)
                break;
            var signature = BitConverter.ToUInt32(bytes, pos + 4);
            if (signature == EnvironmentVariableBlock && size >= 8 + 260 + 520)
            {
                var value = ReadUtf16Z(bytes, pos + 8 + 260);
                if (value.Length == 0)
                    value = ReadAnsiZ(bytes, pos + 8);
                if (value.Length > 0)
                    return Environment.ExpandEnvironmentVariables(value);
            }

            pos += size;
        }

        return null;
    }

    private static string ReadAnsiZ(byte[] bytes, int offset)
    {
        var end = Array.IndexOf(bytes, (byte)0, offset);
        if (end < 0)
            end = bytes.Length;
        return Encoding.Latin1.GetString(bytes, offset, end - offset);
    }

    private static string ReadUtf16Z(byte[] bytes, int offset)
    {
        var end = offset;
        while (end + 1 < bytes.Length && (bytes[end] != 0 || bytes[end + 1] != 0))
            end += 2;
        return Encoding.Unicode.GetString(bytes, offset, end - offset);
    }
}
