using System.Buffers.Binary;
using System.Text;

namespace DSPRE.Core;

/// <summary>Reads and patches one member of a packed NARC in place, for archives the project keeps packed
/// under files/ and edits only a few bytes of. A write never changes a member's size, so the rest of the
/// archive is untouched.</summary>
internal static class NarcFile
{
    public static byte[] ReadMember(string narcPath, int member)
    {
        byte[] narc = File.ReadAllBytes(narcPath);
        (int start, int length) = LocateMember(narc, member, narcPath);
        return narc.AsSpan(start, length).ToArray();
    }

    /// <summary>Every member, read in one pass over the file.</summary>
    public static byte[][] ReadAllMembers(string narcPath)
    {
        byte[] narc = File.ReadAllBytes(narcPath);
        int count = BinaryPrimitives.ReadInt32LittleEndian(narc.AsSpan(BtafOffset(narc, narcPath) + 8));
        var members = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            (int start, int length) = LocateMember(narc, i, narcPath);
            members[i] = narc.AsSpan(start, length).ToArray();
        }

        return members;
    }

    /// <summary>Overwrites <paramref name="data"/>.Length bytes of the member starting at
    /// <paramref name="offset"/>.</summary>
    public static void WriteMember(string narcPath, int member, int offset, ReadOnlySpan<byte> data)
    {
        byte[] narc = File.ReadAllBytes(narcPath);
        (int start, int length) = LocateMember(narc, member, narcPath);
        if (offset < 0 || offset + data.Length > length) throw new InvalidDataException($"{Path.GetFileName(narcPath)} file {member} is {length} bytes, too short for {data.Length} bytes at {offset}.");
        data.CopyTo(narc.AsSpan(start + offset));
        File.WriteAllBytes(narcPath, narc);
    }

    /// <summary>Rewrites the NARC with some members replaced by data of any size; the others and the name table are kept, and every
    /// member starts on a 4-byte boundary as in the originals.</summary>
    public static void ReplaceMembers(string narcPath, IReadOnlyDictionary<int, byte[]> replacements)
    {
        byte[] narc = File.ReadAllBytes(narcPath);
        int btaf = BtafOffset(narc, narcPath);
        int count = BinaryPrimitives.ReadInt32LittleEndian(narc.AsSpan(btaf + 8));
        int btnf = btaf + BinaryPrimitives.ReadInt32LittleEndian(narc.AsSpan(btaf + 4));
        int gmif = btnf + BinaryPrimitives.ReadInt32LittleEndian(narc.AsSpan(btnf + 4));

        var members = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            (int start, int length) = LocateMember(narc, i, narcPath);
            members.Add(replacements.TryGetValue(i, out byte[]? replacement) ? replacement : narc.AsSpan(start, length).ToArray());
        }

        using var data = new MemoryStream();
        var table = new byte[count * 8];
        for (int i = 0; i < count; i++)
        {
            while (data.Length % 4 != 0) data.WriteByte(0xFF);
            BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(i * 8), (int)data.Length);
            data.Write(members[i]);
            BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(i * 8 + 4), (int)data.Length);
        }

        while (data.Length % 4 != 0) data.WriteByte(0xFF);
        using var output = new MemoryStream();
        output.Write(narc.AsSpan(0, btaf + 12));
        output.Write(table);
        output.Write(narc.AsSpan(btnf, gmif - btnf));
        var gmifHeader = new byte[8];
        "GMIF"u8.CopyTo(gmifHeader);
        BinaryPrimitives.WriteInt32LittleEndian(gmifHeader.AsSpan(4), (int)data.Length + 8);
        output.Write(gmifHeader);
        data.WriteTo(output);
        byte[] result = output.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), result.Length);
        File.WriteAllBytes(narcPath, result);
    }

    public static int MemberCount(string narcPath)
    {
        byte[] narc = File.ReadAllBytes(narcPath);
        return BinaryPrimitives.ReadInt32LittleEndian(narc.AsSpan(BtafOffset(narc, narcPath) + 8));
    }

    private static int BtafOffset(byte[] narc, string path)
    {
        int btaf = narc.Length >= 0x10 && Magic(narc, 0) == "NARC" ? BinaryPrimitives.ReadUInt16LittleEndian(narc.AsSpan(0x0C)) : -1;
        if (btaf < 0 || Magic(narc, btaf) != "BTAF") throw new InvalidDataException($"{Path.GetFileName(path)} is not a NARC.");
        return btaf;
    }

    private static string Magic(byte[] narc, int offset) => offset + 4 <= narc.Length ? Encoding.ASCII.GetString(narc, offset, 4) : "";

    private static (int Start, int Length) LocateMember(byte[] narc, int member, string path)
    {
        int btaf = BtafOffset(narc, path);
        int count = BinaryPrimitives.ReadInt32LittleEndian(narc.AsSpan(btaf + 8));
        if (member >= count) throw new InvalidDataException($"{Path.GetFileName(path)} has no file {member}.");
        int btnf = btaf + BinaryPrimitives.ReadInt32LittleEndian(narc.AsSpan(btaf + 4));
        int gmif = btnf + BinaryPrimitives.ReadInt32LittleEndian(narc.AsSpan(btnf + 4));
        if (Magic(narc, gmif) != "GMIF") throw new InvalidDataException($"{Path.GetFileName(path)} is not a NARC.");
        int start = BinaryPrimitives.ReadInt32LittleEndian(narc.AsSpan(btaf + 12 + member * 8));
        int end = BinaryPrimitives.ReadInt32LittleEndian(narc.AsSpan(btaf + 16 + member * 8));
        if (end < start || gmif + 8 + end > narc.Length) throw new InvalidDataException($"{Path.GetFileName(path)} file {member} is out of bounds.");
        return (gmif + 8 + start, end - start);
    }
}
