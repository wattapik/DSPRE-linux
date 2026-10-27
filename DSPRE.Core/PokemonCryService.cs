namespace DSPRE.Core;

/// <summary>Pokémon cries. Gen IV keeps every cry as a small sequence in the sound archive's "SEQ_PV" sequence archive (numbered by
/// species), played through the "BANK_PV" bank whose samples are the cries' recordings; <see cref="NitroSynth"/> plays them.</summary>
public static class PokemonCryService
{
    public sealed record Cry(short[] Samples, int SampleRate)
    {
        public double Seconds => (double)Samples.Length / SampleRate;

        /// <summary>The cry as a 16-bit mono WAV file.</summary>
        public byte[] ToWav()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            int bytes = Samples.Length * 2;
            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + bytes);
            writer.Write("WAVEfmt "u8.ToArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8.ToArray());
            writer.Write(bytes);
            foreach (short sample in Samples) writer.Write(sample);
            return stream.ToArray();
        }
    }

    public static string? FindSoundArchive(RomProject project, RomGameMetadata metadata)
    {
        string[] candidates = metadata.HeaderFormat switch
        {
            MapHeaderFormat.Platinum => [Path.Combine(project.FilesPath, "data", "sound", "pl_sound_data.sdat"), Path.Combine(project.FilesPath, "data", "sound", "sound_data.sdat")],
            MapHeaderFormat.DiamondPearl => [Path.Combine(project.FilesPath, "data", "sound", "sound_data.sdat")],
            MapHeaderFormat.HeartGoldSoulSilver => [Path.Combine(project.FilesPath, "data", "sound", "gs_sound_data.sdat"), Path.Combine(project.FilesPath, "data", "sound", "sound_data.sdat")],
            _ => []
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static bool IsSupported(RomProject project, RomGameMetadata metadata) => FindSoundArchive(project, metadata) is not null;

    /// <summary>Where a species' cry is: its bank's instrument, and the wave that instrument plays, with the room that wave has.</summary>
    private sealed record Located(SdatArchive Archive, SdatArchive.BankInfo Bank, byte[] BankFile, int InstrumentAt, int WaveArchiveFile, byte[] Swar, int WaveStart, int WaveEnd)
    {
        public int Budget => WaveEnd - WaveStart;
    }

    private static byte[] ReadSoundArchive(RomProject project, RomGameMetadata metadata, out string path)
    {
        path = FindSoundArchive(project, metadata) ?? throw new FileNotFoundException("This project has no sound archive.");
        return File.ReadAllBytes(path);
    }

    // A species' bank is the one at its own number: the banks' names (BANK_PVnnn) stop following it from species 387 on, so they are not used.
    private static Located Locate(byte[] sdat, int species)
    {
        SdatArchive archive = SdatArchive.Read(sdat);
        if (species < 0 || species >= archive.Banks.Count || archive.Banks[species].FileId < 0) throw new InvalidOperationException($"The game has no cry for species {species:D3}.");
        SdatArchive.BankInfo bank = archive.Banks[species];
        byte[] bankFile = archive.File(bank.FileId);
        if (bankFile.Length < 0x44 || bankFile[0x3C] != 1) throw new InvalidOperationException($"Species {species:D3}'s cry is not set up the usual way (one sampled instrument).");
        int instrument = BitConverter.ToUInt16(bankFile, 0x3D);
        int slot = BitConverter.ToUInt16(bankFile, instrument + 2), waveIndex = BitConverter.ToUInt16(bankFile, instrument);
        int archiveId = slot < bank.WaveArchives.Length ? bank.WaveArchives[slot] : -1;
        int fileId = archiveId is >= 0 and < 0xFFFF && archiveId < archive.WaveArchives.Count ? archive.WaveArchives[archiveId] : -1;
        if (fileId < 0) throw new InvalidOperationException($"Species {species:D3}'s cry could not be located.");
        byte[] swar = archive.File(fileId);

        // The old wave's place in the file, up to the start of the next one (or the end), is the room there is.
        int count = (int)BitConverter.ToUInt32(swar, 0x38);
        var starts = Enumerable.Range(0, count).Select(i => (int)BitConverter.ToUInt32(swar, 0x3C + 4 * i)).Where(offset => offset > 0).Order().ToList();
        int start = (int)BitConverter.ToUInt32(swar, 0x3C + 4 * waveIndex);
        return new Located(archive, bank, bankFile, instrument, fileId, swar, start, starts.FirstOrDefault(offset => offset > start, swar.Length));
    }

    /// <summary>The cry of a species (its National Pokédex number), or null when the game has none for it. Each species has its own
    /// bank, holding one instrument whose sample is the cry; the game plays it as middle C of its generic sequence <c>SEQ_PV</c>,
    /// so the recording comes out at the pitch it was stored for.</summary>
    /// <param name="holdSeconds">How long the note is held before its envelope releases it; 0 plays the whole sample.</param>
    public static Cry? Render(RomProject project, RomGameMetadata metadata, int species, double holdSeconds = 0)
    {
        string? path = FindSoundArchive(project, metadata);
        return path is null ? null : Render(File.ReadAllBytes(path), species, holdSeconds);
    }

    /// <summary>The cry as it is in the given sound archive (which need not be the one on disk: see <see cref="Preview"/>).</summary>
    public static Cry? Render(byte[] sdat, int species, double holdSeconds = 0)
    {
        if (species < 0) return null;
        SdatArchive archive = SdatArchive.Read(sdat);
        if (species >= archive.Banks.Count) return null;

        SdatArchive.BankInfo bank = archive.Banks[species];
        if (bank.FileId < 0) return null;
        int[] waveArchives = bank.WaveArchives.Select(id => id is >= 0 and < 0xFFFF && id < archive.WaveArchives.Count ? archive.WaveArchives[id] : -1).ToArray();
        var synth = new NitroSynth(archive.File(bank.FileId), waveArchives, archive.File);
        short[] samples = synth.PlayWholeSample(program: 0, key: 60, holdSeconds);
        return samples.Length == 0 ? null : new Cry(samples, NitroSynth.SampleRate);
    }

    /// <summary>The settings that shape a cry: its instrument (the base note the recording is tuned to and the envelope that fades the
    /// sound in and out) and its wave (sample rate, how it is stored and whether it loops).</summary>
    /// <param name="Note">The key at which the recording plays at its own pitch (the game plays middle C, 60).</param>
    /// <param name="Attack">Envelope rates and sustain level, 0 to 127 as in the sound data: 127 is instant (attack, decay, release) or full (sustain).</param>
    /// <param name="Pan">0 left, 64 centre, 127 right.</param>
    /// <param name="LoopStart">Sample the wave goes back to when it loops.</param>
    /// <param name="Format">0 8-bit, 1 16-bit, 2 ADPCM.</param>
    public sealed record Settings(int Note, int Attack, int Decay, int Sustain, int Release, int Pan, int SampleRate, bool Loops, int LoopStart, int Format, int Samples, int Bytes, int Budget)
    {
        public double Seconds => SampleRate == 0 ? 0 : (double)Samples / SampleRate;
    }

    public static Settings ReadSettings(RomProject project, RomGameMetadata metadata, int species) => ReadSettings(ReadSoundArchive(project, metadata, out _), species);

    public static Settings ReadSettings(byte[] sdat, int species)
    {
        Located found = Locate(sdat, species);
        byte[] bank = found.BankFile, wave = found.Swar;
        int at = found.InstrumentAt, start = found.WaveStart;
        int format = wave[start], rate = BitConverter.ToUInt16(wave, start + 2);
        int loopStartWords = BitConverter.ToUInt16(wave, start + 6), loopWords = (int)BitConverter.ToUInt32(wave, start + 8);
        int bytes = (loopStartWords + loopWords) * 4;
        int samples = format == 0 ? bytes : format == 1 ? bytes / 2 : Math.Max(bytes - 4, 0) * 2;
        int loopStart = format == 0 ? loopStartWords * 4 : format == 1 ? loopStartWords * 2 : Math.Max(loopStartWords * 4 - 4, 0) * 2;
        return new Settings(bank[at + 4], bank[at + 5], bank[at + 6], bank[at + 7], bank[at + 8], bank[at + 9], rate, wave[start + 1] != 0, loopStart, format, samples, bytes + 12, found.Budget);
    }

    /// <summary>Writes new instrument and wave settings in place (the archive keeps its size). The recording itself is not touched.</summary>
    public static void ApplySettings(RomProject project, RomGameMetadata metadata, int species, Settings settings)
    {
        byte[] sdat = SettingsApplied(ReadSoundArchive(project, metadata, out string path), species, settings);
        File.WriteAllBytes(path, sdat);
        EditJournal.Record(project, "SetPokemonCrySettings", ("species", species), ("note", settings.Note), ("attack", settings.Attack), ("decay", settings.Decay), ("sustain", settings.Sustain),
            ("release", settings.Release), ("pan", settings.Pan), ("sampleRate", Math.Clamp(settings.SampleRate, 1000, 65535)), ("loops", settings.Loops), ("loopStart", settings.LoopStart));
    }

    /// <summary>The sound archive with the settings applied, nothing written.</summary>
    public static byte[] SettingsApplied(byte[] sdat, int species, Settings settings)
    {
        Located found = Locate(sdat, species);
        byte[] bank = (byte[])found.BankFile.Clone();
        int at = found.InstrumentAt;
        bank[at + 4] = (byte)Math.Clamp(settings.Note, 0, 127);
        bank[at + 5] = (byte)Math.Clamp(settings.Attack, 0, 127);
        bank[at + 6] = (byte)Math.Clamp(settings.Decay, 0, 127);
        bank[at + 7] = (byte)Math.Clamp(settings.Sustain, 0, 127);
        bank[at + 8] = (byte)Math.Clamp(settings.Release, 0, 127);
        bank[at + 9] = (byte)Math.Clamp(settings.Pan, 0, 127);

        byte[] wave = (byte[])found.Swar.Clone();
        int start = found.WaveStart;
        int rate = Math.Clamp(settings.SampleRate, 1000, 65535);
        BitConverter.TryWriteBytes(wave.AsSpan(start + 2), (ushort)rate);
        BitConverter.TryWriteBytes(wave.AsSpan(start + 4), (ushort)Math.Clamp(16756991 / rate, 0, 65535));
        int totalWords = BitConverter.ToUInt16(wave, start + 6) + (int)BitConverter.ToUInt32(wave, start + 8);
        if (settings.Loops)
        {
            int format = wave[start];
            int startBytes = format == 0 ? settings.LoopStart : format == 1 ? settings.LoopStart * 2 : 4 + settings.LoopStart / 2;
            int words = Math.Clamp(startBytes / 4, 0, Math.Max(totalWords - 1, 0));
            wave[start + 1] = 1;
            BitConverter.TryWriteBytes(wave.AsSpan(start + 6), (ushort)words);
            BitConverter.TryWriteBytes(wave.AsSpan(start + 8), (uint)(totalWords - words));
        }
        else
        {
            wave[start + 1] = 0;
            BitConverter.TryWriteBytes(wave.AsSpan(start + 6), (ushort)0);
            BitConverter.TryWriteBytes(wave.AsSpan(start + 8), (uint)totalWords);
        }

        byte[] withBank = found.Archive.WithFile(found.Bank.FileId, bank);
        return SdatArchive.Read(withBank).WithFile(found.WaveArchiveFile, wave);
    }

    /// <param name="Format">How the new recording is stored: 0 8-bit (what the original cries use), 1 16-bit, 2 ADPCM (four bits a sample, so the most sound in the room).</param>
    /// <param name="SampleRate">The rate to store it at; 0 keeps the old cry's.</param>
    /// <param name="GainDb">Volume change before storing; samples that would go past full scale are limited.</param>
    /// <param name="Normalize">Raises the recording until its loudest point is at full scale (before the gain).</param>
    public sealed record ImportOptions(int Format = 2, int SampleRate = 0, double GainDb = 0, bool Normalize = false);

    /// <param name="Seconds">How much of the new sound is in the game.</param>
    /// <param name="Cut">The recording was longer than the cry's room, so its end was left off.</param>
    public sealed record ImportResult(double Seconds, double OriginalSeconds, bool Cut, int Bytes, int Budget, int SampleRate);

    /// <summary>Replaces a species' cry with the sound of a WAV file. The recording is stored in the chosen format and has to fit the room
    /// the old cry took in the sound archive (nothing else in the archive moves), so a longer recording is cut off, with a short fade at the cut.</summary>
    public static ImportResult Import(RomProject project, RomGameMetadata metadata, int species, string wavPath, ImportOptions? options = null)
    {
        options ??= new ImportOptions();
        (byte[] sdat, ImportResult result) = ImportInto(ReadSoundArchive(project, metadata, out string path), species, wavPath, options);
        File.WriteAllBytes(path, sdat);
        EditJournal.Record(project, "ImportPokemonCry", ("species", species), ("source", Path.GetFullPath(wavPath)), ("format", options.Format), ("sampleRate", options.SampleRate), ("gainDb", options.GainDb), ("normalize", options.Normalize));
        return result;
    }

    /// <summary>The sound archive with the cry replaced, nothing written.</summary>
    public static (byte[] Sdat, ImportResult Result) ImportInto(byte[] sdat, int species, string wavPath, ImportOptions options)
    {
        Located found = Locate(sdat, species);
        byte[] swar = found.Swar;
        int start = found.WaveStart, budget = found.Budget;
        int oldRate = Math.Max((int)BitConverter.ToUInt16(swar, start + 2), 1);
        int rate = options.SampleRate > 0 ? Math.Clamp(options.SampleRate, 1000, 65535) : oldRate;
        double originalSeconds = ReadSettings(sdat, species).Seconds;

        WavFile.Audio audio = WavFile.Read(File.ReadAllBytes(wavPath));
        float gain = (float)Math.Pow(10, options.GainDb / 20);
        if (options.Normalize)
        {
            float peak = audio.Samples.Length == 0 ? 0 : audio.Samples.Max(sample => Math.Abs(sample));
            if (peak > 0) gain *= 1f / peak;
        }

        int room = budget - 12;
        int maxSamples = options.Format switch
        {
            0 => room / 4 * 4,
            1 => room / 4 * 4 / 2,
            _ => ((room / 4 * 4) - 4) * 2
        };
        if (maxSamples < 16) throw new InvalidOperationException("The cry has no room to replace.");
        int wanted = (int)Math.Min((long)(audio.Samples.Length / (double)audio.SampleRate * rate), int.MaxValue);
        int length = Math.Min(wanted, maxSamples);
        var pcm = new short[length];
        for (int i = 0; i < length; i++)
        {
            double position = i * (double)audio.SampleRate / rate;
            int index = (int)position;
            float a = audio.Samples[Math.Min(index, audio.Samples.Length - 1)], b = audio.Samples[Math.Min(index + 1, audio.Samples.Length - 1)];
            pcm[i] = (short)Math.Clamp((a + (b - a) * (position - index)) * gain * 32767f, short.MinValue, short.MaxValue);
        }

        bool cut = wanted > maxSamples;
        if (cut)
        {
            int fade = Math.Min(rate / 60, length);
            for (int i = 0; i < fade; i++) pcm[length - fade + i] = (short)(pcm[length - fade + i] * (1.0 - (i + 1.0) / fade));
        }

        byte[] encoded = options.Format switch
        {
            0 => Pad(pcm.Select(sample => (byte)(sbyte)Math.Clamp((sample + 128) >> 8, -128, 127)).ToArray()),
            1 => Pad(pcm.SelectMany(sample => BitConverter.GetBytes(sample)).ToArray()),
            _ => NitroSynth.EncodeAdpcm(pcm)
        };

        byte[] result = (byte[])swar.Clone();
        Array.Clear(result, start, found.WaveEnd - start);
        result[start] = (byte)options.Format;
        result[start + 1] = 0;
        BitConverter.TryWriteBytes(result.AsSpan(start + 2), (ushort)rate);
        BitConverter.TryWriteBytes(result.AsSpan(start + 4), (ushort)Math.Clamp(16756991 / rate, 0, 65535));
        BitConverter.TryWriteBytes(result.AsSpan(start + 6), (ushort)0);
        BitConverter.TryWriteBytes(result.AsSpan(start + 8), (uint)(encoded.Length / 4));
        encoded.CopyTo(result, start + 12);
        return (found.Archive.WithFile(found.WaveArchiveFile, result), new ImportResult(length / (double)rate, originalSeconds, cut, encoded.Length + 12, budget, rate));
    }

    /// <summary>A cry change waiting to be saved: a replacement recording and/or new settings (applied after it).</summary>
    public sealed record Pending(string? WavPath, ImportOptions? Import, Settings? Settings);

    /// <summary>The sound archive as it will be once the pending change is saved, nothing written.</summary>
    public static byte[] Preview(byte[] sdat, int species, Pending pending)
    {
        if (pending.WavPath is not null && pending.Import is not null) sdat = ImportInto(sdat, species, pending.WavPath, pending.Import).Sdat;
        if (pending.Settings is not null) sdat = SettingsApplied(sdat, species, pending.Settings);
        return sdat;
    }

    /// <summary>Writes a pending change, journaling each part.</summary>
    public static void Save(RomProject project, RomGameMetadata metadata, int species, Pending pending)
    {
        if (pending.WavPath is not null && pending.Import is not null) Import(project, metadata, species, pending.WavPath, pending.Import);
        if (pending.Settings is not null) ApplySettings(project, metadata, species, pending.Settings);
    }

    private static byte[] Pad(byte[] data) => data.Length % 4 == 0 ? data : [.. data, .. new byte[4 - data.Length % 4]];
}
