using System.Buffers.Binary;
using System.Text;

namespace DSPRE.Core;

/// <summary>Adds, removes and extracts the textures (TEX0 block) embedded in an NSBMD model. Ported
/// from legacy DS_Map/DSUtils/NSBUtils.cs (BuildNSBMDwithTextures, GetModelWithoutTextures,
/// GetTexturesFromTexturedNSBMD), which the legacy NSBMD Utility menu used.</summary>
public static class NsbmdTextureTools
{
    private const ushort HeaderSize = 0x10;

    public static bool IsNsbmd(byte[] data) => data.Length >= 0x14 && Encoding.ASCII.GetString(data, 0, 4) == "BMD0";
    public static bool IsNsbtx(byte[] data) => data.Length >= 0x14 && Encoding.ASCII.GetString(data, 0, 4) == "BTX0";

    /// <summary>A model with more than one block carries its own TEX0 after the MDL0.</summary>
    public static bool HasTextures(byte[] nsbmd) => BinaryPrimitives.ReadUInt16LittleEndian(nsbmd.AsSpan(0x0E)) >= 2;

    /// <summary>The model's MDL0 followed by the NSBTX's TEX0, in a fresh two-block BMD0 file. Any
    /// textures the model had are replaced.</summary>
    public static byte[] WithTextures(byte[] nsbmd, byte[] nsbtx)
    {
        RequireNsbmd(nsbmd);
        if (!IsNsbtx(nsbtx)) throw new InvalidDataException("The texture file is not an NSBTX (BTX0) file.");

        byte[] mdl0 = FirstBlock(nsbmd);
        byte[] tex0 = FirstBlock(nsbtx);
        const ushort blockCount = 2;
        int modelLength = mdl0.Length + HeaderSize + 4 * blockCount;
        byte[] output = new byte[modelLength + tex0.Length];
        Span<byte> span = output;
        WriteHeader(span, "BMD0", (uint)output.Length, blockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x10..], HeaderSize + 4 * blockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x14..], (uint)modelLength);
        mdl0.CopyTo(span[(HeaderSize + 4 * blockCount)..]);
        tex0.CopyTo(span[modelLength..]);
        return output;
    }

    /// <summary>The model with its embedded TEX0 dropped, as a one-block BMD0 file.</summary>
    public static byte[] WithoutTextures(byte[] nsbmd)
    {
        RequireNsbmd(nsbmd);
        byte[] mdl0 = FirstBlock(nsbmd);
        byte[] output = new byte[0x14 + mdl0.Length];
        Span<byte> span = output;
        WriteHeader(span, "BMD0", (uint)output.Length, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x10..], 0x14);
        mdl0.CopyTo(span[0x14..]);
        return output;
    }

    /// <summary>The model's embedded TEX0 as a standalone NSBTX file, or null when it has none.</summary>
    public static byte[]? ExtractTextures(byte[] nsbmd)
    {
        RequireNsbmd(nsbmd);
        if (!HasTextures(nsbmd)) return null;

        int tex0 = (int)BinaryPrimitives.ReadUInt32LittleEndian(nsbmd.AsSpan(0x14));
        int tex0Length = (int)BinaryPrimitives.ReadUInt32LittleEndian(nsbmd.AsSpan(tex0 + 4));
        byte[] output = new byte[0x14 + tex0Length];
        Span<byte> span = output;
        WriteHeader(span, "BTX0", (uint)output.Length, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x10..], 0x14);
        nsbmd.AsSpan(tex0, tex0Length).CopyTo(span[0x14..]);
        return output;
    }

    private static void RequireNsbmd(byte[] data)
    {
        if (!IsNsbmd(data)) throw new InvalidDataException("The model file is not an NSBMD (BMD0) file.");
    }

    // Byte-order mark, then the format version legacy wrote: 2 for BMD0, 1 for BTX0.
    private static void WriteHeader(Span<byte> span, string magic, uint fileSize, ushort blockCount)
    {
        Encoding.ASCII.GetBytes(magic).CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 0xFEFF);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], magic == "BTX0" ? (ushort)0x0001 : (ushort)0x0002);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], fileSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], blockCount);
    }

    private static byte[] FirstBlock(byte[] file)
    {
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x10));
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset + 4));
        return file.AsSpan(offset, length).ToArray();
    }
}
