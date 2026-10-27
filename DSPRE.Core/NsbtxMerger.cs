using System.Buffers.Binary;
using System.Text;

namespace DSPRE.Core;

/// <summary>Adds textures (and the palettes they are drawn with) from one NSBTX texture pack to another, as the raw bytes they have
/// in the source, and writes the result with its two name dictionaries built again. A model finds its textures and palettes by name,
/// through those dictionaries (a Patricia tree over the names as 128-bit little-endian numbers); <see cref="BuildDictionary"/> makes
/// a tree the same search resolves for every name, checked against all the dictionaries in a real ROM. Packs with 4x4-compressed textures are
/// not handled.</summary>
public static class NsbtxMerger
{
    private const int HeaderSize = 0x3C;

    public sealed class Pack
    {
        internal byte[] Header = [];
        public List<string> TextureNames { get; } = [];
        internal List<byte[]> TextureEntries { get; } = [];
        internal byte[] TextureData = [];
        public List<string> PaletteNames { get; } = [];
        internal List<byte[]> PaletteEntries { get; } = [];
        internal byte[] PaletteData = [];
    }

    public sealed record Result(byte[] Data, IReadOnlyList<string> AddedTextures, IReadOnlyList<string> AddedPalettes, IReadOnlyList<string> Conflicts);

    public static Pack Parse(byte[] nsbtx)
    {
        if (nsbtx.Length < 0x14 || Encoding.ASCII.GetString(nsbtx, 0, 4) != "BTX0") throw new InvalidDataException("Not an NSBTX (BTX0) file.");
        int tex0 = (int)BinaryPrimitives.ReadUInt32LittleEndian(nsbtx.AsSpan(0x10));
        if (tex0 + HeaderSize > nsbtx.Length || Encoding.ASCII.GetString(nsbtx, tex0, 4) != "TEX0") throw new InvalidDataException("The NSBTX file has no TEX0 block.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(nsbtx.AsSpan(tex0 + 0x1C)) != 0) throw new InvalidDataException("Texture packs with 4x4-compressed textures cannot be merged.");

        var pack = new Pack { Header = nsbtx.AsSpan(tex0, HeaderSize).ToArray() };
        int textureDictionary = tex0 + BinaryPrimitives.ReadUInt16LittleEndian(nsbtx.AsSpan(tex0 + 0x0E));
        int paletteDictionary = tex0 + (int)BinaryPrimitives.ReadUInt32LittleEndian(nsbtx.AsSpan(tex0 + 0x34));
        ReadDictionary(nsbtx, textureDictionary, pack.TextureNames, pack.TextureEntries);
        ReadDictionary(nsbtx, paletteDictionary, pack.PaletteNames, pack.PaletteEntries);

        int textureDataOffset = tex0 + (int)BinaryPrimitives.ReadUInt32LittleEndian(nsbtx.AsSpan(tex0 + 0x14));
        pack.TextureData = nsbtx.AsSpan(textureDataOffset, BinaryPrimitives.ReadUInt16LittleEndian(nsbtx.AsSpan(tex0 + 0x0C)) << 3).ToArray();
        int paletteDataOffset = tex0 + (int)BinaryPrimitives.ReadUInt32LittleEndian(nsbtx.AsSpan(tex0 + 0x38));
        pack.PaletteData = nsbtx.AsSpan(paletteDataOffset, BinaryPrimitives.ReadUInt16LittleEndian(nsbtx.AsSpan(tex0 + 0x30)) << 3).ToArray();
        return pack;
    }

    private static void ReadDictionary(byte[] data, int dictionary, List<string> names, List<byte[]> entries)
    {
        int count = data[dictionary + 1];
        int dataBlock = dictionary + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(dictionary + 6));
        int entrySize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(dataBlock));
        int nameStart = dataBlock + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(dataBlock + 2));
        for (int i = 0; i < count; i++)
        {
            entries.Add(data.AsSpan(dataBlock + 4 + entrySize * i, entrySize).ToArray());
            names.Add(Encoding.ASCII.GetString(data, nameStart + 16 * i, 16).TrimEnd('\0', ' '));
        }
    }

    /// <summary>The target pack with the named textures and palettes of the source added (those it already has by name are left as they
    /// are; a same-named one whose bytes differ is reported in <see cref="Result.Conflicts"/>).</summary>
    public static Result Add(byte[] target, byte[] source, IEnumerable<string> textures, IEnumerable<string> palettes)
    {
        Pack into = Parse(target), from = Parse(source);
        var addedTextures = new List<string>();
        var addedPalettes = new List<string>();
        var conflicts = new List<string>();

        using var textureData = new MemoryStream();
        textureData.Write(into.TextureData);
        AddItems(into.TextureNames, into.TextureEntries, into.TextureData, from.TextureNames, from.TextureEntries, from.TextureData, textures, textureData, addedTextures, conflicts, "texture");
        into.TextureData = textureData.ToArray();

        using var paletteData = new MemoryStream();
        paletteData.Write(into.PaletteData);
        AddItems(into.PaletteNames, into.PaletteEntries, into.PaletteData, from.PaletteNames, from.PaletteEntries, from.PaletteData, palettes, paletteData, addedPalettes, conflicts, "palette");
        into.PaletteData = paletteData.ToArray();
        return new Result(Build(into), addedTextures, addedPalettes, conflicts);
    }

    private static void AddItems(List<string> names, List<byte[]> entries, byte[] data, List<string> sourceNames, List<byte[]> sourceEntries, byte[] sourceData,
        IEnumerable<string> wanted, MemoryStream output, List<string> added, List<string> conflicts, string kind)
    {
        var copied = new Dictionary<int, int>();
        foreach (string name in wanted.Distinct())
        {
            int sourceIndex = sourceNames.IndexOf(name);
            if (sourceIndex < 0) continue;
            byte[] entry = sourceEntries[sourceIndex].ToArray();
            int existing = names.IndexOf(name);
            if (existing >= 0)
            {
                if (!Same(Slice(entries[existing], data, Extent(entries, data.Length, existing)), Slice(entry, sourceData, Extent(sourceEntries, sourceData.Length, sourceIndex)))) conflicts.Add($"{kind} {name}");
                continue;
            }

            int offset = BinaryPrimitives.ReadUInt16LittleEndian(entry) << 3;
            if (!copied.TryGetValue(offset, out int newOffset))
            {
                byte[] blob = Slice(entry, sourceData, Extent(sourceEntries, sourceData.Length, sourceIndex));
                newOffset = (int)output.Length;
                output.Write(blob);
                while (output.Length % 8 != 0) output.WriteByte(0);
                copied[offset] = newOffset;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)(newOffset >> 3));
            names.Add(name);
            entries.Add(entry);
            added.Add(name);
        }
    }

    private static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

    /// <summary>Where an entry's data runs to: the next higher offset among the entries, or the end of the data.</summary>
    private static int Extent(List<byte[]> entries, int dataLength, int index)
    {
        int start = BinaryPrimitives.ReadUInt16LittleEndian(entries[index]) << 3;
        int next = entries.Select(entry => BinaryPrimitives.ReadUInt16LittleEndian(entry) << 3).Where(offset => offset > start).DefaultIfEmpty(dataLength).Min();
        return next - start;
    }

    private static byte[] Slice(byte[] entry, byte[] data, int length) => data.AsSpan(BinaryPrimitives.ReadUInt16LittleEndian(entry) << 3, length).ToArray();

    public static byte[] Build(Pack pack)
    {
        byte[] textureDictionary = BuildDictionary(pack.TextureNames, pack.TextureEntries, 8);
        byte[] paletteDictionary = BuildDictionary(pack.PaletteNames, pack.PaletteEntries, 4);
        byte[] textureData = Padded(pack.TextureData), paletteData = Padded(pack.PaletteData);
        int paletteDictionaryOffset = HeaderSize + textureDictionary.Length;
        int textureDataOffset = paletteDictionaryOffset + paletteDictionary.Length;
        int paletteDataOffset = textureDataOffset + textureData.Length;
        int blockSize = paletteDataOffset + paletteData.Length;

        byte[] block = new byte[blockSize];
        pack.Header.CopyTo(block, 0);
        Span<byte> span = block;
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)blockSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x0C..], (ushort)(textureData.Length >> 3));
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x0E..], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x14..], (uint)textureDataOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x1C..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x1E..], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x24..], (uint)paletteDataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x28..], (uint)paletteDataOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x30..], (ushort)(paletteData.Length >> 3));
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x34..], (uint)paletteDictionaryOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x38..], (uint)paletteDataOffset);
        textureDictionary.CopyTo(span[HeaderSize..]);
        paletteDictionary.CopyTo(span[paletteDictionaryOffset..]);
        textureData.CopyTo(span[textureDataOffset..]);
        paletteData.CopyTo(span[paletteDataOffset..]);

        byte[] file = new byte[0x14 + blockSize];
        Encoding.ASCII.GetBytes("BTX0").CopyTo(file, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), 0xFEFF);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)file.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(12), 0x10);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 0x14);
        block.CopyTo(file, 0x14);
        return file;
    }

    private static byte[] Padded(byte[] data)
    {
        if (data.Length % 8 == 0) return data;
        byte[] padded = new byte[(data.Length + 7) & ~7];
        data.CopyTo(padded, 0);
        return padded;
    }

    /// <summary>A name dictionary: header, the search tree (a root that always goes left, then one node per name), the entries and the names.</summary>
    public static byte[] BuildDictionary(IReadOnlyList<string> names, IReadOnlyList<byte[]> entries, int entrySize)
    {
        int count = names.Count;
        byte[][] keys = names.Select(name => { byte[] key = new byte[16]; Encoding.ASCII.GetBytes(name.Length > 16 ? name[..16] : name).CopyTo(key, 0); return key; }).ToArray();
        byte[][] nodes = BuildTree(keys);
        int treeSection = 4 + 8 + 4 * count;
        int dataSection = 4 + entrySize * count;
        byte[] output = new byte[treeSection + dataSection + 16 * count];
        output[1] = (byte)count;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2), (ushort)output.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(6), (ushort)treeSection);
        for (int i = 0; i < nodes.Length; i++) nodes[i].CopyTo(output, 8 + 4 * i);
        int dataBlock = treeSection;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(dataBlock), (ushort)entrySize);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(dataBlock + 2), (ushort)dataSection);
        for (int i = 0; i < count; i++)
        {
            entries[i].CopyTo(output, dataBlock + 4 + entrySize * i);
            keys[i].CopyTo(output, dataBlock + dataSection + 16 * i);
        }

        return output;
    }

    /// <summary>The nodes of the Patricia tree over the keys: node 0 is the root (bit 127, always left), node i+1 stands for key i. A node is
    /// (bit tested, left, right, key index); a key is looked up by walking from the root, taking right where the key's bit is set, until
    /// a node's bit is not lower than the one before.</summary>
    internal static byte[][] BuildTree(byte[][] keys)
    {
        var nodes = new List<int[]> { new[] { 127, 0, 0, 0 } };
        int Bit(byte[] key, int n) => (key[n >> 3] >> (n & 7)) & 1;
        int Child(int node, byte[] key) => Bit(key, nodes[node][0]) == 1 ? nodes[node][2] : nodes[node][1];

        for (int index = 0; index < keys.Length; index++)
        {
            byte[] key = keys[index];
            int previous = 0, node = Child(0, key);
            while (nodes[node][0] < nodes[previous][0])
            {
                previous = node;
                node = Child(previous, key);
            }

            byte[] other = node == 0 ? new byte[16] : keys[nodes[node][3]];
            int bit = -1;
            for (int b = 15; b >= 0 && bit < 0; b--)
            {
                int difference = key[b] ^ other[b];
                if (difference != 0) bit = b * 8 + (int)Math.Log2(difference);
            }

            if (bit < 0) throw new InvalidDataException($"Two names in the dictionary are the same ({Encoding.ASCII.GetString(key).TrimEnd('\0')}).");
            previous = 0;
            node = Child(0, key);
            while (nodes[node][0] < nodes[previous][0] && nodes[node][0] > bit)
            {
                previous = node;
                node = Child(previous, key);
            }

            int added = nodes.Count;
            bool set = Bit(key, bit) == 1;
            nodes.Add([bit, set ? node : added, set ? added : node, index]);
            if (Bit(key, nodes[previous][0]) == 1) nodes[previous][2] = added;
            else nodes[previous][1] = added;
        }

        return nodes.Select(node => new[] { (byte)node[0], (byte)node[1], (byte)node[2], (byte)node[3] }).ToArray();
    }

    /// <summary>The key index a name resolves to in the dictionary tree <see cref="BuildTree"/> made (or one read from a file).</summary>
    internal static int Search(byte[][] nodes, string name)
    {
        byte[] key = new byte[16];
        Encoding.ASCII.GetBytes(name.Length > 16 ? name[..16] : name).CopyTo(key, 0);
        int node = 0;
        while (true)
        {
            int previous = node;
            node = ((key[nodes[previous][0] >> 3] >> (nodes[previous][0] & 7)) & 1) == 1 ? nodes[previous][2] : nodes[previous][1];
            if (nodes[node][0] >= nodes[previous][0]) return nodes[node][3];
        }
    }
}
