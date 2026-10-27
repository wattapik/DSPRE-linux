using System.Buffers.Binary;
using System.Text;

namespace DSPRE.Core;

/// <summary>A Nitro sound archive (SDAT): sequences, instrument banks and wave archives, with their names, found through the
/// archive's symbol table and record table, and the files they refer to.</summary>
public sealed class SdatArchive
{
    public sealed record SequenceInfo(int Index, string Name, int FileId, int Bank, int Volume);
    public sealed record BankInfo(int FileId, int[] WaveArchives);

    private readonly byte[] data;
    private readonly (int Offset, int Size)[] files;

    public IReadOnlyList<SequenceInfo> Sequences { get; }
    public IReadOnlyList<BankInfo> Banks { get; }
    public IReadOnlyList<int> WaveArchives { get; }

    public IReadOnlyList<string> BankNames { get; }

    private SdatArchive(byte[] data, (int, int)[] files, List<SequenceInfo> sequences, List<BankInfo> banks, List<int> waves, List<string> bankNames)
    {
        this.data = data;
        this.files = files;
        Sequences = sequences;
        Banks = banks;
        WaveArchives = waves;
        BankNames = bankNames;
    }

    public static SdatArchive Read(byte[] data)
    {
        if (data.Length < 0x40 || Encoding.ASCII.GetString(data, 0, 4) != "SDAT") throw new InvalidDataException("Not a sound archive (SDAT).");
        int U32(int at) => (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
        int U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
        int symb = U32(0x10), info = U32(0x18), fat = U32(0x20);

        string Name(int symbolTable, int index)
        {
            if (symb <= 0) return string.Empty;
            int table = symb + U32(symb + 8 + 4 * symbolTable);
            if (index >= U32(table)) return string.Empty;
            int start = symb + U32(table + 4 + 4 * index), end = start;
            while (end < data.Length && data[end] != 0) end++;
            return Encoding.ASCII.GetString(data, start, end - start);
        }

        // The record table of a kind: count, then an offset (from the INFO block) to each record; 0 marks an unused slot.
        List<int> Records(int kind)
        {
            int table = info + U32(info + 8 + 4 * kind);
            int count = U32(table);
            var offsets = new List<int>();
            for (int i = 0; i < count; i++) offsets.Add(U32(table + 4 + 4 * i) is int record and > 0 ? info + record : -1);
            return offsets;
        }

        int fileCount = U32(fat + 8);
        var files = new (int, int)[fileCount];
        for (int i = 0; i < fileCount; i++) files[i] = (U32(fat + 12 + 16 * i), U32(fat + 16 + 16 * i));

        var sequences = new List<SequenceInfo>();
        List<int> sequenceRecords = Records(0);
        for (int i = 0; i < sequenceRecords.Count; i++)
        {
            if (sequenceRecords[i] < 0) continue;
            int at = sequenceRecords[i];
            sequences.Add(new SequenceInfo(i, Name(0, i), U16(at), U16(at + 4), data[at + 6]));
        }

        var banks = new List<BankInfo>();
        foreach (int at in Records(2))
        {
            banks.Add(at < 0 ? new BankInfo(-1, [-1, -1, -1, -1]) : new BankInfo(U16(at), [U16(at + 4), U16(at + 6), U16(at + 8), U16(at + 10)]));
        }

        var waves = new List<int>();
        foreach (int at in Records(3)) waves.Add(at < 0 ? -1 : U16(at));
        return new SdatArchive(data, files, sequences, banks, waves, Enumerable.Range(0, banks.Count).Select(i => Name(2, i)).ToList());
    }

    public byte[] File(int fileId)
    {
        if (fileId < 0 || fileId >= files.Length) throw new InvalidDataException($"The sound archive has no file {fileId}.");
        (int offset, int size) = files[fileId];
        return data.AsSpan(offset, size).ToArray();
    }

    /// <summary>Where a file sits in the archive, and how many bytes it has.</summary>
    public (int Offset, int Size) FileLocation(int fileId) => fileId >= 0 && fileId < files.Length ? files[fileId] : throw new InvalidDataException($"The sound archive has no file {fileId}.");

    /// <summary>The archive with a file's contents replaced by <paramref name="content"/>, which may be shorter (the rest is zeroed) but
    /// not longer: every other file stays where it is.</summary>
    public byte[] WithFile(int fileId, ReadOnlySpan<byte> content)
    {
        (int offset, int size) = FileLocation(fileId);
        if (content.Length > size) throw new InvalidDataException("The new file is larger than the one it replaces.");
        byte[] copy = (byte[])data.Clone();
        Array.Clear(copy, offset, size);
        content.CopyTo(copy.AsSpan(offset));
        return copy;
    }

    /// <summary>The archive with a file's contents replaced by <paramref name="content"/> of any size: a larger file takes room from the
    /// archive's end by moving the files after it, whose positions (and the archive's sizes) are rewritten.</summary>
    public byte[] WithFileResized(int fileId, ReadOnlySpan<byte> content)
    {
        (int offset, int size) = FileLocation(fileId);
        if (content.Length <= size) return WithFile(fileId, content);

        int U32(int at) => (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
        void Set(byte[] target, int at, int value) => BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(at), (uint)value);
        int fat = U32(0x20), fileBlock = U32(0x28);
        int slotEnd = fileBlock + U32(fileBlock + 4);
        foreach ((int other, _) in files)
        {
            if (other > offset && other < slotEnd) slotEnd = other;
        }

        int slot = slotEnd - offset;
        int alignment = files.All(file => file.Offset % 32 == 0) ? 32 : 4;
        int newSlot = content.Length <= slot ? slot : (content.Length + alignment - 1) / alignment * alignment;
        int delta = newSlot - slot;

        byte[] result = new byte[data.Length + delta];
        data.AsSpan(0, offset).CopyTo(result);
        content.CopyTo(result.AsSpan(offset));
        data.AsSpan(slotEnd).CopyTo(result.AsSpan(offset + newSlot));
        for (int i = 0; i < files.Length; i++)
        {
            if (files[i].Offset > offset) Set(result, fat + 12 + 16 * i, files[i].Offset + delta);
        }

        Set(result, fat + 16 + 16 * fileId, content.Length);
        Set(result, 0x08, data.Length + delta);
        Set(result, 0x2C, U32(0x2C) + delta);
        Set(result, fileBlock + 4, U32(fileBlock + 4) + delta);
        return result;
    }
}
