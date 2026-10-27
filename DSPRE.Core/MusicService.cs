namespace DSPRE.Core;

/// <summary>The game's music and sound effects: the sequences of the sound archive, played by <see cref="NitroSequencer"/>.</summary>
public static class MusicService
{
    public sealed record Entry(SdatArchive.SequenceInfo Info, string Category)
    {
        public override string ToString() => $"{Info.Name}  [{Category}]";
    }

    public sealed record Rendered(short[] Stereo, int SampleRate)
    {
        public double Seconds => (double)Stereo.Length / 2 / SampleRate;

        /// <summary>The sound as a 16-bit stereo WAV file.</summary>
        public byte[] ToWav()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            int bytes = Stereo.Length * 2;
            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + bytes);
            writer.Write("WAVEfmt "u8.ToArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)2);
            writer.Write(SampleRate);
            writer.Write(SampleRate * 4);
            writer.Write((short)4);
            writer.Write((short)16);
            writer.Write("data"u8.ToArray());
            writer.Write(bytes);
            foreach (short sample in Stereo) writer.Write(sample);
            return stream.ToArray();
        }
    }

    public static bool IsSupported(RomProject project, RomGameMetadata metadata) => PokemonCryService.FindSoundArchive(project, metadata) is not null;

    private static string CategoryOf(string name) =>
        name.StartsWith("SEQ_SE_", StringComparison.Ordinal) ? "Sound effect"
        : name.StartsWith("SEQ_ME_", StringComparison.Ordinal) ? "Jingle"
        : name.StartsWith("SEQ_PV", StringComparison.Ordinal) ? "Cry"
        : name.Contains("_D", StringComparison.Ordinal) || name.StartsWith("SEQ_", StringComparison.Ordinal) ? "Music"
        : "Other";

    public static IReadOnlyList<Entry> List(RomProject project, RomGameMetadata metadata)
    {
        string path = PokemonCryService.FindSoundArchive(project, metadata) ?? throw new FileNotFoundException("This project has no sound archive.");
        SdatArchive archive = SdatArchive.Read(File.ReadAllBytes(path));
        return archive.Sequences.Where(sequence => sequence.Name.Length > 0).Select(sequence => new Entry(sequence, CategoryOf(sequence.Name))).ToList();
    }

    public static Rendered Render(RomProject project, RomGameMetadata metadata, int sequenceIndex, NitroSequencer.Options options, byte[]? sequenceFile = null)
    {
        string path = PokemonCryService.FindSoundArchive(project, metadata) ?? throw new FileNotFoundException("This project has no sound archive.");
        SdatArchive archive = SdatArchive.Read(File.ReadAllBytes(path));
        SdatArchive.SequenceInfo sequence = archive.Sequences.First(info => info.Index == sequenceIndex);
        SdatArchive.BankInfo bank = archive.Banks[sequence.Bank];
        int[] waveFiles = bank.WaveArchives.Select(id => id is >= 0 and < 0xFFFF && id < archive.WaveArchives.Count ? archive.WaveArchives[id] : -1).ToArray();
        var sequencer = new NitroSequencer(archive.File(bank.FileId), waveFiles, archive.File, options.BankEntrySize);
        return new Rendered(sequencer.Render(sequenceFile ?? archive.File(sequence.FileId), sequence.Volume, options), NitroSequencer.SampleRate);
    }

    /// <param name="Sequences">Names of the sequences that play with this bank.</param>
    public sealed record BankEntry(int Index, int FileId, IReadOnlyList<int> WaveArchives, IReadOnlyList<string> Sequences);

    public static IReadOnlyList<BankEntry> Banks(RomProject project, RomGameMetadata metadata)
    {
        string path = PokemonCryService.FindSoundArchive(project, metadata) ?? throw new FileNotFoundException("This project has no sound archive.");
        SdatArchive archive = SdatArchive.Read(File.ReadAllBytes(path));
        return archive.Banks.Select((bank, index) => new BankEntry(index, bank.FileId, bank.WaveArchives,
            archive.Sequences.Where(sequence => sequence.Bank == index && sequence.Name.Length > 0).Select(sequence => sequence.Name).ToList())).ToList();
    }

    private static NitroSequencer OpenBank(RomProject project, RomGameMetadata metadata, int bankIndex, int bankEntrySize)
    {
        string path = PokemonCryService.FindSoundArchive(project, metadata) ?? throw new FileNotFoundException("This project has no sound archive.");
        SdatArchive archive = SdatArchive.Read(File.ReadAllBytes(path));
        if (bankIndex < 0 || bankIndex >= archive.Banks.Count) throw new ArgumentException("There is no such instrument bank.", nameof(bankIndex));
        SdatArchive.BankInfo bank = archive.Banks[bankIndex];
        int[] waveFiles = bank.WaveArchives.Select(id => id is >= 0 and < 0xFFFF && id < archive.WaveArchives.Count ? archive.WaveArchives[id] : -1).ToArray();
        return new NitroSequencer(archive.File(bank.FileId), waveFiles, archive.File, bankEntrySize);
    }

    public static IReadOnlyList<NitroSequencer.InstrumentSummary> BankInstruments(RomProject project, RomGameMetadata metadata, int bankIndex, int bankEntrySize = 4) =>
        OpenBank(project, metadata, bankIndex, bankEntrySize).Instruments();

    public static Rendered RenderBankNote(RomProject project, RomGameMetadata metadata, int bankIndex, int program, int key, double holdSeconds, int bankEntrySize = 4) =>
        new(OpenBank(project, metadata, bankIndex, bankEntrySize).RenderNote(program, key, 100, holdSeconds), NitroSequencer.SampleRate);

    private static (NitroSequencer Sequencer, SdatArchive.SequenceInfo Sequence) OpenSequencer(RomProject project, RomGameMetadata metadata, int sequenceIndex, int bankEntrySize)
    {
        string path = PokemonCryService.FindSoundArchive(project, metadata) ?? throw new FileNotFoundException("This project has no sound archive.");
        SdatArchive archive = SdatArchive.Read(File.ReadAllBytes(path));
        SdatArchive.SequenceInfo sequence = archive.Sequences.First(info => info.Index == sequenceIndex);
        SdatArchive.BankInfo bank = archive.Banks[sequence.Bank];
        int[] waveFiles = bank.WaveArchives.Select(id => id is >= 0 and < 0xFFFF && id < archive.WaveArchives.Count ? archive.WaveArchives[id] : -1).ToArray();
        return (new NitroSequencer(archive.File(bank.FileId), waveFiles, archive.File, bankEntrySize), sequence);
    }

    public static NitroSequencer.SequenceView Notes(RomProject project, RomGameMetadata metadata, int sequenceIndex, NitroSequencer.Options options, byte[]? sequenceFile = null)
    {
        (NitroSequencer sequencer, SdatArchive.SequenceInfo sequence) = OpenSequencer(project, metadata, sequenceIndex, options.BankEntrySize);
        string path = PokemonCryService.FindSoundArchive(project, metadata)!;
        return sequencer.Analyze(sequenceFile ?? SdatArchive.Read(File.ReadAllBytes(path)).File(sequence.FileId), sequence.Volume, options);
    }

    /// <summary>The instruments of the bank a sequence plays with, and the notes (key ranges) each one answers to.</summary>
    public static IReadOnlyList<NitroSequencer.InstrumentSummary> Instruments(RomProject project, RomGameMetadata metadata, int sequenceIndex, int bankEntrySize = 4) =>
        OpenSequencer(project, metadata, sequenceIndex, bankEntrySize).Sequencer.Instruments();

    public static Rendered RenderNote(RomProject project, RomGameMetadata metadata, int sequenceIndex, int program, int key, double holdSeconds, int bankEntrySize = 4) =>
        new(OpenSequencer(project, metadata, sequenceIndex, bankEntrySize).Sequencer.RenderNote(program, key, 100, holdSeconds), NitroSequencer.SampleRate);

    /// <summary>Writes the sequence's own files: the sequence (.sseq), its bank (.sbnk) and the wave archives the bank uses (.swar).</summary>
    public static IReadOnlyList<string> ExportFiles(RomProject project, RomGameMetadata metadata, int sequenceIndex, string folder)
    {
        string path = PokemonCryService.FindSoundArchive(project, metadata) ?? throw new FileNotFoundException("This project has no sound archive.");
        SdatArchive archive = SdatArchive.Read(File.ReadAllBytes(path));
        SdatArchive.SequenceInfo sequence = archive.Sequences.First(info => info.Index == sequenceIndex);
        SdatArchive.BankInfo bank = archive.Banks[sequence.Bank];
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        void Write(string name, byte[] data)
        {
            string file = Path.Combine(folder, name);
            File.WriteAllBytes(file, data);
            written.Add(file);
        }

        Write(sequence.Name + ".sseq", archive.File(sequence.FileId));
        Write($"BANK_{sequence.Bank:D4}.sbnk", archive.File(bank.FileId));
        foreach (int id in bank.WaveArchives.Distinct().Where(id => id is >= 0 and < 0xFFFF && id < archive.WaveArchives.Count && archive.WaveArchives[id] >= 0))
        {
            Write($"WAVE_ARC_{id:D4}.swar", archive.File(archive.WaveArchives[id]));
        }

        return written;
    }

    /// <summary>The sequence's file as it is in the sound archive.</summary>
    public static byte[] ReadSequence(RomProject project, RomGameMetadata metadata, int sequenceIndex)
    {
        string path = PokemonCryService.FindSoundArchive(project, metadata) ?? throw new FileNotFoundException("This project has no sound archive.");
        SdatArchive archive = SdatArchive.Read(File.ReadAllBytes(path));
        return archive.File(archive.Sequences.First(info => info.Index == sequenceIndex).FileId);
    }

    /// <summary>Applies note edits one after the other (each offset is into the sequence as the edits before it left it) and writes the result
    /// into the sound archive, journaling each.</summary>
    public static void EditNotes(RomProject project, RomGameMetadata metadata, int sequenceIndex, IReadOnlyList<SequenceEditor.NoteOp> operations)
    {
        string path = PokemonCryService.FindSoundArchive(project, metadata) ?? throw new FileNotFoundException("This project has no sound archive.");
        SdatArchive archive = SdatArchive.Read(File.ReadAllBytes(path));
        SdatArchive.SequenceInfo sequence = archive.Sequences.First(info => info.Index == sequenceIndex);
        byte[] file = archive.File(sequence.FileId);
        foreach (SequenceEditor.NoteOp operation in operations) file = SequenceEditor.Apply(file, operation);
        File.WriteAllBytes(path, archive.WithFileResized(sequence.FileId, file));
        foreach (SequenceEditor.NoteOp operation in operations)
        {
            if (operation.Delete) EditJournal.Record(project, "DeleteSequenceNote", ("sequence", sequenceIndex), ("offset", operation.Offset), ("waits", operation.Waits));
            else EditJournal.Record(project, operation.Create ? "AddSequenceNote" : "SetSequenceNote", ("sequence", sequenceIndex), ("offset", operation.Offset), ("key", operation.Key), ("velocity", operation.Velocity), ("length", operation.Length),
                ("waits", operation.Waits), ("shift", operation.Shift), ("program", operation.Program ?? -1), ("currentProgram", operation.CurrentProgram));
        }
    }
}
