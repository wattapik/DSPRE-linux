using System.Buffers.Binary;
using System.Text;

namespace DSPRE.Core;

public static class NarcPacker
{
    public static void Pack(string inputDirectory, string outputPath)
    {
        string[] files = Directory.GetFiles(inputDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".backup", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        byte[][] contents = files.Select(File.ReadAllBytes).ToArray();
        int count = contents.Length;
        int fatbSize = 12 + count * 8;
        int fntbSize = 16;
        int fimgDataSize = contents.Sum(data => Align4(data.Length));
        int fimgSize = 8 + fimgDataSize;
        int totalSize = 16 + fatbSize + fntbSize + fimgSize;
        byte[] output = new byte[totalSize];
        Span<byte> span = output;
        Encoding.ASCII.GetBytes("NARC").CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 0x0100FFFE);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)totalSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], 3);
        int offset = 16;
        WriteBlockHeader(span, offset, "BTAF", fatbSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 8)..], (uint)count);
        int dataOffset = 16 + fatbSize + fntbSize + 8;
        int current = 0;
        for (int i = 0; i < count; i++)
        {
            current = Align4(current);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 12 + i * 8)..], (uint)current);
            current += contents[i].Length;
            BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 16 + i * 8)..], (uint)current);
        }
        offset += fatbSize;
        WriteBlockHeader(span, offset, "BTNF", fntbSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 8)..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 12)..], 0x10000);
        offset += fntbSize;
        WriteBlockHeader(span, offset, "GMIF", fimgSize);
        current = 0;
        foreach (byte[] content in contents)
        {
            int aligned = Align4(current);
            while (current < aligned) output[offset + 8 + current++] = 0xFF;
            content.CopyTo(output, offset + 8 + current);
            current += content.Length;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllBytes(outputPath, output);
    }

    private static void WriteBlockHeader(Span<byte> output, int offset, string magic, int size)
    {
        Encoding.ASCII.GetBytes(magic).CopyTo(output[offset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(output[(offset + 4)..], (uint)size);
    }

    private static int Align4(int value) => (value + 3) & ~3;
}
