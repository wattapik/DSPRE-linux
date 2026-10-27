using System.Buffers.Binary;

namespace DSPRE.Core;

public static class NarcExtractor
{
    public static int Extract(string narcPath, string outputDirectory)
    {
        byte[] data = File.ReadAllBytes(narcPath);
        if (data.Length < 24 || System.Text.Encoding.ASCII.GetString(data, 0, 4) != "NARC")
        {
            throw new InvalidDataException($"'{narcPath}' is not a NARC archive.");
        }

        int offset = 16;
        int fatbOffset = FindBlock(data, "BTAF", offset);
        int fileCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(fatbOffset + 8));
        int entriesOffset = fatbOffset + 12;
        int fimgOffset = FindBlock(data, "GMIF", entriesOffset + fileCount * 8);
        int dataOffset = fimgOffset + 8;

        Directory.CreateDirectory(outputDirectory);
        for (int i = 0; i < fileCount; i++)
        {
            int start = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(entriesOffset + i * 8));
            int end = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(entriesOffset + i * 8 + 4));
            int length = end - start;
            if (start < 0 || length < 0 || dataOffset + end > data.Length)
            {
                throw new InvalidDataException($"NARC file entry {i} is outside the archive bounds.");
            }

            File.WriteAllBytes(Path.Combine(outputDirectory, i.ToString("D4")), data.AsSpan(dataOffset + start, length).ToArray());
        }

        return fileCount;
    }

    private static int FindBlock(byte[] data, string magic, int start)
    {
        byte[] expected = System.Text.Encoding.ASCII.GetBytes(magic);
        for (int i = start; i <= data.Length - 8; i++)
        {
            if (data.AsSpan(i, 4).SequenceEqual(expected))
            {
                return i;
            }
        }

        throw new InvalidDataException($"NARC block '{magic}' was not found.");
    }
}
