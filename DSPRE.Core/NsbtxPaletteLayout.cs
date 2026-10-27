using System.Buffers.Binary;
using System.Text;

namespace DSPRE.Core;

/// <summary>Where each palette's colours sit inside an NSBTX (BTX0) file, so a palette can be rewritten
/// in place without rebuilding the file.</summary>
public static class NsbtxPaletteLayout
{
    public sealed record PaletteSlot(string Name, int Offset, int Length);

    /// <summary>Every palette in the file, with its absolute byte offset and length (2 bytes a colour).</summary>
    public static List<PaletteSlot> Read(byte[] data)
    {
        if (data.Length < 0x14 || Encoding.ASCII.GetString(data, 0, 4) != "BTX0")
        {
            throw new InvalidDataException("Not an NSBTX (BTX0) file.");
        }

        int tex0 = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x10));
        if (tex0 + 0x3C > data.Length || Encoding.ASCII.GetString(data, tex0, 4) != "TEX0")
        {
            throw new InvalidDataException("The NSBTX file has no TEX0 block.");
        }

        // TEX0 header: palette data size (in 8-byte units) at +0x30, palette dictionary offset at +0x34,
        // palette data offset at +0x38 - both offsets relative to the TEX0 block.
        int paletteDataSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(tex0 + 0x30)) << 3;
        int dictionary = tex0 + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(tex0 + 0x34));
        int paletteData = tex0 + (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(tex0 + 0x38));

        // A 3D-info dictionary: dummy, count, size, then an unknown block (header size, section size,
        // constant, 4 bytes per entry), then the data block (header size, data size, 4 bytes per entry),
        // then a 16-byte name per entry.
        int count = data[dictionary + 1];
        int unknownBlock = dictionary + 4;
        int dataBlock = unknownBlock + 8 + 4 * count;
        int entries = dataBlock + 4;
        int names = entries + 4 * count;

        var offsets = new int[count];
        var slotNames = new string[count];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(entries + 4 * i)) << 3;
            slotNames[i] = Encoding.ASCII.GetString(data, names + 16 * i, 16).TrimEnd('\0', ' ');
        }

        // Palettes are stored back to back, so each runs up to the next one's start (in offset order,
        // which need not be name order); the last runs to the end of the palette data.
        var sortedStarts = offsets.Distinct().Order().ToList();
        var slots = new List<PaletteSlot>(count);
        for (int i = 0; i < count; i++)
        {
            int next = sortedStarts.FirstOrDefault(start => start > offsets[i], paletteDataSize);
            int length = Math.Max(0, Math.Min(next, paletteDataSize) - offsets[i]);
            slots.Add(new PaletteSlot(slotNames[i], paletteData + offsets[i], length));
        }

        return slots;
    }

    /// <summary>The names of the textures in the file, in the order its dictionary lists them.</summary>
    public static List<string> ReadTextureNames(byte[] data)
    {
        if (data.Length < 0x14 || Encoding.ASCII.GetString(data, 0, 4) != "BTX0") throw new InvalidDataException("Not an NSBTX (BTX0) file.");
        int tex0 = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x10));
        if (tex0 + 0x3C > data.Length || Encoding.ASCII.GetString(data, tex0, 4) != "TEX0") throw new InvalidDataException("The NSBTX file has no TEX0 block.");

        int dictionary = tex0 + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(tex0 + 0x0E));
        int count = data[dictionary + 1];
        int names = dictionary + 4 + 8 + 4 * count + 4 + 8 * count;
        return Enumerable.Range(0, count).Select(i => Encoding.ASCII.GetString(data, names + 16 * i, 16).TrimEnd('\0', ' ')).ToList();
    }
}
