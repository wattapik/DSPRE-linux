using System.Buffers.Binary;
using System.Text;

namespace DSPRE.Core;

/// <summary>A software version of the DS's music engine: plays a Nitro sequence (SSEQ commands) through an instrument bank (SBNK) whose
/// samples are in wave archives (SWAR), and returns the mixed result as stereo 16-bit samples. It follows the engine closely enough to
/// recognise a tune (notes, programs, volume, expression, pan, pitch bend, transpose, vibrato, tempo, loops, jumps, calls, variables and
/// the usual conditions, sampled instruments, pulse and noise generators); envelopes are approximations and portamento, reverb and the
/// DS's mixing limits are left out.</summary>
public sealed class NitroSequencer
{
    public const int SampleRate = 32768;

    /// <param name="BankEntrySize">Bytes between instrument entries when the bank is read. The format uses 4; other sizes pick other instruments
    /// than the game does (8 gives the harsh, chip-like sound of the first version of the player).</param>
    public sealed record Options(double MaxSeconds = 180, int LoopRepeats = 0, int BankEntrySize = 4);

    private sealed class Wave
    {
        public int Type;
        public bool Loops;
        public int Rate;
        public int LoopStart;
        public short[] Samples = [];
    }

    private sealed record Region(int Type, int Wave, int WaveArchive, int Note, int Attack, int Decay, int Sustain, int Release, int Pan);

    private readonly List<List<(int Low, int High, Region Region)>?> instruments = [];
    private readonly List<int> instrumentTypes = [];
    private readonly Dictionary<int, List<Wave>> waveArchives = [];
    private readonly byte[] bankFile;

    public NitroSequencer(byte[] bank, int[] archiveFileIds, Func<int, byte[]> file, int bankEntrySize = 4)
    {
        bankFile = bank;
        ReadBank(bank, Math.Max(bankEntrySize, 4));
        for (int slot = 0; slot < archiveFileIds.Length; slot++)
        {
            if (archiveFileIds[slot] < 0 || waveArchives.ContainsKey(slot)) continue;
            try
            {
                waveArchives[slot] = ReadWaveArchive(file(archiveFileIds[slot]));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                waveArchives[slot] = [];
            }
        }
    }

    #region Banks and waves

    private void ReadBank(byte[] file, int entrySize)
    {
        if (file.Length < 0x3C || Encoding.ASCII.GetString(file, 0, 4) != "SBNK") throw new InvalidDataException("Not an instrument bank (SBNK).");
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x38));
        for (int i = 0; i < count; i++)
        {
            int at = 0x3C + entrySize * i;
            if (at + 4 > file.Length) break;
            int type = file[at], offset = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 1));
            instrumentTypes.Add(type);
            if (type == 0 || offset == 0 || offset + 10 > file.Length)
            {
                instruments.Add(null);
                continue;
            }

            var regions = new List<(int, int, Region)>();
            Region RegionAt(int regionType, int p) => new(regionType, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(p)), BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(p + 2)), file[p + 4], file[p + 5], file[p + 6], file[p + 7], file[p + 8], file[p + 9]);
            if (type is >= 1 and <= 5)
            {
                regions.Add((0, 127, RegionAt(type, offset)));
            }
            else if (type == 16 && offset + 2 <= file.Length)
            {
                int low = file[offset], high = file[offset + 1];
                for (int note = low; note <= high; note++)
                {
                    int p = offset + 2 + (note - low) * 12;
                    if (p + 12 <= file.Length) regions.Add((note, note, RegionAt(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(p)), p + 2)));
                }
            }
            else if (type == 17 && offset + 8 <= file.Length)
            {
                int low = 0;
                for (int r = 0; r < 8 && file[offset + r] != 0; r++)
                {
                    int p = offset + 8 + r * 12;
                    if (p + 12 > file.Length) break;
                    regions.Add((low, file[offset + r], RegionAt(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(p)), p + 2)));
                    low = file[offset + r] + 1;
                }
            }

            instruments.Add(regions);
        }
    }

    private static List<Wave> ReadWaveArchive(byte[] file)
    {
        if (file.Length < 0x3C || Encoding.ASCII.GetString(file, 0, 4) != "SWAR") throw new InvalidDataException("Not a wave archive (SWAR).");
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x38));
        var waves = new List<Wave>();
        for (int i = 0; i < count; i++)
        {
            int at = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x3C + 4 * i));
            if (at <= 0 || at + 12 > file.Length)
            {
                waves.Add(new Wave());
                continue;
            }

            var wave = new Wave
            {
                Type = file[at],
                Loops = file[at + 1] != 0,
                Rate = Math.Max(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 2)), (ushort)1),
                LoopStart = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 6)) * 4
            };
            int loopLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 8)) * 4;
            int dataStart = at + 12, dataLength = Math.Max(Math.Min(wave.LoopStart + loopLength, file.Length - dataStart), 0);
            wave.Samples = wave.Type switch
            {
                0 => Enumerable.Range(0, dataLength).Select(k => (short)((sbyte)file[dataStart + k] << 8)).ToArray(),
                1 => Enumerable.Range(0, dataLength / 2).Select(k => BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(dataStart + 2 * k))).ToArray(),
                _ => NitroSynth.DecodeAdpcm(file.AsSpan(dataStart, dataLength))
            };
            wave.LoopStart = wave.Type == 0 ? wave.LoopStart : wave.Type == 1 ? wave.LoopStart / 2 : Math.Max(wave.LoopStart - 4, 0) * 2;
            waves.Add(wave);
        }

        return waves;
    }

    #endregion

    #region Note view

    /// <param name="Offset">Where the note command sits in the sequence data; a note played again by a loop or a called section has the same one.</param>
    /// <param name="Transpose">What the track added to the stored key, so the stored key is <c>Key - Transpose</c> unless it was clamped.</param>
    /// <param name="Waits">The track moves on by the note's length (usual); otherwise the note starts and the track carries on at once.</param>
    public sealed record NoteEvent(int Track, int Program, int Key, int Velocity, int Start, int Length, int Offset = -1, int Transpose = 0, bool Waits = true);
    public sealed record TempoEvent(int Tick, double Tempo);

    /// <param name="Ticks">The sequence's own time unit: 48 to a quarter note.</param>
    /// <param name="LoopTick">Where the sequence jumps back to its loop (the first backward jump), or -1 when it never does.</param>
    public sealed record SequenceView(IReadOnlyList<NoteEvent> Notes, IReadOnlyList<TempoEvent> Tempos, int LoopTick, int EndTick, int TrackCount);

    private List<NoteEvent>? noteLog;
    private List<TempoEvent>? tempoLog;
    private int tickCounter;
    private int commandOffset;
    private int loopTick = -1;

    /// <summary>Runs a sequence as <see cref="Render(byte[], int, Options)"/> does and returns the notes it starts instead of the sound.</summary>
    public SequenceView Analyze(byte[] sequenceFile, int volume, Options options)
    {
        noteLog = [];
        tempoLog = [new TempoEvent(0, 120)];
        tickCounter = 0;
        loopTick = -1;
        short[] samples = Render(sequenceFile, volume, options);
        var view = new SequenceView(noteLog, tempoLog, loopTick, noteLog.Count == 0 ? 0 : noteLog.Max(note => note.Start + note.Length), noteLog.Count == 0 ? 0 : noteLog.Max(note => note.Track) + 1);
        noteLog = null;
        tempoLog = null;
        return view;
    }

    #endregion

    #region Instruments

    public sealed record WaveSummary(int Type, int Rate, bool Loops, int Samples);

    /// <param name="Kind">PCM sample, pulse wave, noise, or a type this player does not make sound with.</param>
    public sealed record RegionSummary(int Low, int High, string Kind, int Wave, int WaveArchive, int BaseNote, int Attack, int Decay, int Sustain, int Release, int Pan, WaveSummary? Sample);

    /// <param name="Layout">Single sound, drum set (one sound per key) or key split (sounds by key range).</param>
    public sealed record InstrumentSummary(int Program, string Layout, IReadOnlyList<RegionSummary> Regions);

    public IReadOnlyList<InstrumentSummary> Instruments()
    {
        var result = new List<InstrumentSummary>();
        for (int program = 0; program < instruments.Count; program++)
        {
            if (instruments[program] is not { } regions) continue;
            string layout = instrumentTypes[program] switch { 16 => "Drum set", 17 => "Key split", _ => "Single sound" };
            result.Add(new InstrumentSummary(program, layout, regions.Select(entry =>
            {
                Region region = entry.Region;
                WaveSummary? sample = null;
                if (region.Type == 1 && waveArchives.TryGetValue(region.WaveArchive, out List<Wave>? waves) && region.Wave < waves.Count)
                {
                    Wave wave = waves[region.Wave];
                    sample = new WaveSummary(wave.Type, wave.Rate, wave.Loops, wave.Samples.Length);
                }

                string kind = region.Type switch { 1 => "PCM sample", 2 => "Pulse wave", 3 => "Noise", _ => "Unsupported" };
                return new RegionSummary(entry.Low, entry.High, kind, region.Wave, region.WaveArchive, region.Note, region.Attack, region.Decay, region.Sustain, region.Release, region.Pan, sample);
            }).ToList()));
        }

        return result;
    }

    /// <summary>One note of an instrument, as stereo samples: the key is held for <paramref name="holdSeconds"/> and then released.</summary>
    public short[] RenderNote(int program, int key, int velocity, double holdSeconds)
    {
        int ticks = Math.Max(1, (int)(holdSeconds * 2 * 48));
        var data = new List<byte>();
        void Variable(int value)
        {
            var parts = new Stack<byte>();
            parts.Push((byte)(value & 0x7F));
            for (value >>= 7; value > 0; value >>= 7) parts.Push((byte)(0x80 | (value & 0x7F)));
            data.AddRange(parts);
        }

        data.Add(0x81);
        Variable(program);
        data.Add((byte)Math.Clamp(key, 0, 127));
        data.Add((byte)Math.Clamp(velocity, 1, 127));
        Variable(ticks);
        data.Add(0xFF);
        return Render(data.ToArray(), 0, 127, new Options(holdSeconds + 6));
    }

    #endregion

    #region Sequencing

    private sealed class Voice
    {
        public Region Region = null!;
        public Wave? Wave;
        public double Position, Increment;
        public double BasePitch;
        public double SweepStart;
        public int Tick, Length, Track;
        public double Velocity, Level;
        public bool Released, Decaying, Done;
        public int Key;
        public uint Noise = 0x7FFF;
        public double Phase;
        public double Pan;
    }

    private sealed class Track
    {
        public int Position;
        public int Wait;
        public bool Active = true;
        public int Program;
        public int Volume = 127, Expression = 127, Pan = 64, MainVolume = 127;
        public int Transpose;
        public int Bend;
        public int BendRange = 2;
        public int Attack = -1, Decay = -1, Sustain = -1, Release = -1;
        public int SweepPitch;
        public bool NoteWait = true;
        public int ModDepth, ModSpeed = 16, ModType, ModRange = 1, ModDelay;
        public bool Condition = true;
        public readonly Stack<int> Calls = new();
        public readonly Stack<(int Return, int Count)> Loops = new();
        public readonly int[] Variables = new int[16];
        public bool JumpedBack;
        public bool Skip;
    }

    /// <param name="sequenceFile">An SSEQ file.</param>
    /// <param name="volume">The sequence's own volume from the archive (0-127).</param>
    /// <returns>Interleaved stereo samples.</returns>
    public short[] Render(byte[] sequenceFile, int volume, Options options)
    {
        if (sequenceFile.Length < 0x1C || Encoding.ASCII.GetString(sequenceFile, 0, 4) != "SSEQ") throw new InvalidDataException("Not a sequence (SSEQ).");
        int payload = (int)BinaryPrimitives.ReadUInt32LittleEndian(sequenceFile.AsSpan(0x18));
        byte[] data = sequenceFile.AsSpan(payload).ToArray();
        return Render(data, 0, volume, options);
    }

    public short[] Render(byte[] data, int start, int volume, Options options)
    {
        var tracks = new List<Track> { new() { Position = start } };
        var globals = new int[32];
        double tempo = 120;
        double samplesPerTick = SampleRate * 60.0 / (tempo * 48);
        double nextTick = 0;
        var voices = new List<Voice>();
        var left = new List<short>();
        var right = new List<short>();
        int maxSamples = (int)(options.MaxSeconds * SampleRate);
        double master = Math.Pow(volume / 127.0, 2);
        int guard = 0;
        int firstLoopTrack = -1, loopsSeen = 0;
        bool stopping = false;
        int tail = 0;

        for (int sample = 0; sample < maxSamples; sample++)
        {
            if (sample >= nextTick)
            {
                foreach (Track track in tracks.ToList())
                {
                    if (!track.Active) continue;
                    if (track.Wait > 0) track.Wait--;
                    while (track.Active && track.Wait == 0 && guard++ < 5_000_000)
                    {
                        Step(track, data, tracks, voices, globals, ref tempo);
                        if (track.JumpedBack)
                        {
                            track.JumpedBack = false;
                            int index = tracks.IndexOf(track);
                            if (firstLoopTrack < 0) firstLoopTrack = index;
                            if (loopTick < 0 && index == firstLoopTrack) loopTick = tickCounter;
                            if (index == firstLoopTrack)
                            {
                                loopsSeen++;
                                if (loopsSeen > options.LoopRepeats) stopping = true;
                            }
                        }
                    }
                }

                foreach (Voice voice in voices) voice.Tick++;
                tickCounter++;
                samplesPerTick = SampleRate * 60.0 / (tempo * 48);
                nextTick += samplesPerTick;
                if (voices.RemoveAll(voice => voice.Done) > 0) { }
                if (tracks.All(track => !track.Active) && voices.Count == 0) break;
                if (stopping)
                {
                    // Let what is sounding die away, then end.
                    foreach (Track track in tracks) track.Active = false;
                    foreach (Voice voice in voices) voice.Released = true;
                    stopping = false;
                }
            }

            double l = 0, r = 0;
            for (int v = 0; v < voices.Count; v++)
            {
                Voice voice = voices[v];
                if (voice.Done) continue;
                double value = Mix(voice, tracks[Math.Min(voice.Track, tracks.Count - 1)], samplesPerTick);
                double pan = voice.Pan;
                l += value * Math.Cos(pan * Math.PI / 2);
                r += value * Math.Sin(pan * Math.PI / 2);
            }

            left.Add((short)Math.Clamp(l * master * 9000, short.MinValue, short.MaxValue));
            right.Add((short)Math.Clamp(r * master * 9000, short.MinValue, short.MaxValue));
            if (tracks.All(track => !track.Active) && voices.All(voice => voice.Done || voice.Released) && ++tail > SampleRate * 3) break;
        }

        var output = new short[left.Count * 2];
        for (int i = 0; i < left.Count; i++)
        {
            output[2 * i] = left[i];
            output[2 * i + 1] = right[i];
        }

        return output;
    }

    private static int ReadVariable(byte[] data, ref int position)
    {
        int value = 0;
        byte b;
        do
        {
            b = data[position++];
            value = (value << 7) | (b & 0x7F);
        }
        while ((b & 0x80) != 0);
        return value;
    }

    private static int Offset24(byte[] data, ref int position)
    {
        int offset = data[position] | (data[position + 1] << 8) | (data[position + 2] << 16);
        position += 3;
        return offset;
    }

    private void Step(Track track, byte[] data, List<Track> tracks, List<Voice> voices, int[] globals, ref double tempo)
    {
        if (track.Position < 0 || track.Position >= data.Length)
        {
            track.Active = false;
            return;
        }

        commandOffset = track.Position;
        byte command = data[track.Position++];
        // An 'if' prefix runs the command after it only when the last comparison held; the command is still read to move past it.
        bool run = !track.Skip;
        track.Skip = false;

        if (command == 0xA2)
        {
            track.Skip = !track.Condition;
            return;
        }

        if (command == 0xA0 || command == 0xA1)
        {
            // Random / variable argument prefixes: give the next command's first argument.
            if (track.Position >= data.Length) { track.Active = false; return; }
            return;
        }

        if (command < 0x80)
        {
            int velocity = data[track.Position++];
            int length = ReadVariable(data, ref track.Position);
            if (run) StartNote(track, tracks.IndexOf(track), command, velocity, length, voices);
            if (track.NoteWait) track.Wait = Math.Max(length, 1);
            return;
        }

        int ReadArgument(int width)
        {
            int value = width == 1 ? data[track.Position] : width == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(track.Position)) : data[track.Position] | (data[track.Position + 1] << 8) | (data[track.Position + 2] << 16);
            track.Position += width;
            return value;
        }

        switch (command)
        {
            case 0x80: { int wait = ReadVariable(data, ref track.Position); if (run) track.Wait = wait; break; }
            case 0x81: { int program = ReadVariable(data, ref track.Position); if (run) track.Program = program; break; }
            case 0x93:
            {
                ReadArgument(1);
                int offset = Offset24(data, ref track.Position);
                if (run && tracks.Count < 16) tracks.Add(new Track { Position = offset, Program = track.Program });
                break;
            }
            case 0x94:
            {
                int offset = Offset24(data, ref track.Position);
                if (run)
                {
                    // A jump back to earlier in the data is the tune's loop point.
                    track.JumpedBack = offset < track.Position;
                    track.Position = offset;
                }

                break;
            }
            case 0x95:
            {
                int offset = Offset24(data, ref track.Position);
                if (run)
                {
                    track.Calls.Push(track.Position);
                    track.Position = offset;
                }

                break;
            }
            case 0xC0: { int v = ReadArgument(1); if (run) track.Pan = v; break; }
            case 0xC1: { int v = ReadArgument(1); if (run) track.Volume = v; break; }
            case 0xC2: { int v = ReadArgument(1); if (run) track.MainVolume = v; break; }
            case 0xC3: { int v = (sbyte)ReadArgument(1); if (run) track.Transpose = v; break; }
            case 0xC4: { int v = (sbyte)ReadArgument(1); if (run) track.Bend = v; break; }
            case 0xC5: { int v = ReadArgument(1); if (run) track.BendRange = v; break; }
            case 0xC7: { int v = ReadArgument(1); if (run) track.NoteWait = v != 0; break; }
            case 0xCA: { int v = ReadArgument(1); if (run) track.ModDepth = v; break; }
            case 0xCB: { int v = ReadArgument(1); if (run) track.ModSpeed = v; break; }
            case 0xCC: { int v = ReadArgument(1); if (run) track.ModType = v; break; }
            case 0xCD: { int v = ReadArgument(1); if (run) track.ModRange = v; break; }
            case 0xC6: case 0xC8: case 0xC9: case 0xCE: case 0xCF: case 0xD6: ReadArgument(1); break;
            case 0xD0: { int v = ReadArgument(1); if (run) track.Attack = v; break; }
            case 0xD1: { int v = ReadArgument(1); if (run) track.Decay = v; break; }
            case 0xD2: { int v = ReadArgument(1); if (run) track.Sustain = v; break; }
            case 0xD3: { int v = ReadArgument(1); if (run) track.Release = v; break; }
            case 0xD4: { int count = ReadArgument(1); if (run) track.Loops.Push((track.Position, count)); break; }
            case 0xD5: { int v = ReadArgument(1); if (run) track.Expression = v; break; }
            case 0xE0: { int v = ReadArgument(2); if (run) track.ModDelay = v; break; }
            case 0xE1: { int v = ReadArgument(2); if (run) { tempo = Math.Max(v, 1); tempoLog?.Add(new TempoEvent(tickCounter, tempo)); } break; }
            case 0xE3: { int v = (short)ReadArgument(2); if (run) track.SweepPitch = v; break; }
            case 0xFC:
                if (run && track.Loops.Count > 0)
                {
                    (int back, int count) = track.Loops.Pop();
                    if (count == 0 || count > 1)
                    {
                        track.Loops.Push((back, count == 0 ? 0 : count - 1));
                        track.Position = back;
                    }
                }

                break;
            case 0xFD:
                if (run)
                {
                    if (track.Calls.Count > 0) track.Position = track.Calls.Pop();
                    else track.Active = false;
                }

                break;
            case 0xFE: ReadArgument(2); break;
            case 0xFF: track.Active = false; break;
            default:
                if (command >= 0xB0 && command <= 0xBD)
                {
                    int variable = ReadArgument(1);
                    int value = (short)ReadArgument(2);
                    if (!run) break;
                    ref int target = ref (variable < 16 ? ref track.Variables[variable] : ref globals[Math.Min(variable - 16, globals.Length - 1)]);
                    switch (command)
                    {
                        case 0xB0: target = value; break;
                        case 0xB1: target += value; break;
                        case 0xB2: target -= value; break;
                        case 0xB3: target *= value; break;
                        case 0xB4: if (value != 0) target /= value; break;
                        case 0xB5: target = value >= 0 ? target << value : target >> -value; break;
                        case 0xB6: target = value >= 0 ? Random.Shared.Next(value + 1) : -Random.Shared.Next(-value + 1); break;
                        case 0xB8: track.Condition = target == value; break;
                        case 0xB9: track.Condition = target >= value; break;
                        case 0xBA: track.Condition = target > value; break;
                        case 0xBB: track.Condition = target <= value; break;
                        case 0xBC: track.Condition = target < value; break;
                        case 0xBD: track.Condition = target != value; break;
                    }
                }
                else
                {
                    track.Active = false;
                }

                break;
        }
    }

    private void StartNote(Track track, int trackIndex, int key, int velocity, int length, List<Voice> voices)
    {
        List<(int Low, int High, Region Region)>? regions = track.Program >= 0 && track.Program < instruments.Count ? instruments[track.Program] : null;
        if (regions is null) return;
        int shifted = Math.Clamp(key + track.Transpose, 0, 127);
        noteLog?.Add(new NoteEvent(trackIndex, track.Program, shifted, velocity, tickCounter, length, commandOffset, track.Transpose, track.NoteWait));
        if (noteLog is not null) return;
        Region? region = regions.Where(entry => shifted >= entry.Low && shifted <= entry.High).Select(entry => entry.Region).FirstOrDefault();
        if (region is null) return;

        Region effective = region with
        {
            Attack = track.Attack >= 0 ? track.Attack : region.Attack,
            Decay = track.Decay >= 0 ? track.Decay : region.Decay,
            Sustain = track.Sustain >= 0 ? track.Sustain : region.Sustain,
            Release = track.Release >= 0 ? track.Release : region.Release
        };
        Wave? wave = null;
        if (region.Type == 1)
        {
            if (!waveArchives.TryGetValue(region.WaveArchive, out List<Wave>? waves) || region.Wave >= waves.Count || waves[region.Wave].Samples.Length == 0) return;
            wave = waves[region.Wave];
        }

        voices.RemoveAll(existing => existing.Done);
        if (voices.Count >= 64) voices.RemoveAt(0);
        double pan = Math.Clamp((track.Pan + region.Pan - 64) / 127.0, 0, 1);
        voices.Add(new Voice
        {
            Region = effective,
            Wave = wave,
            BasePitch = region.Type == 1 ? shifted - region.Note : shifted,
            SweepStart = track.SweepPitch / 64.0,
            Length = Math.Max(length, 1),
            Track = trackIndex,
            Velocity = velocity / 127.0,
            Key = shifted,
            Pan = pan
        });
    }

    private static double Milliseconds(int rate, double fast, double slow) => rate >= 127 ? 0 : rate >= 126 ? fast : fast + (126 - rate) * slow;

    private static double Mix(Voice voice, Track track, double samplesPerTick)
    {
        Region region = voice.Region;
        double pitch = voice.BasePitch + track.Bend * track.BendRange / 128.0;
        if (voice.SweepStart != 0 && voice.Tick < voice.Length) pitch += voice.SweepStart * (1.0 - (double)voice.Tick / voice.Length);
        if (track.ModDepth > 0 && track.ModType == 0 && voice.Tick * samplesPerTick / SampleRate * 1000 >= track.ModDelay * 10)
        {
            double seconds = voice.Tick * samplesPerTick / SampleRate;
            pitch += Math.Sin(2 * Math.PI * seconds * track.ModSpeed / 16.0) * track.ModDepth / 127.0 * track.ModRange;
        }

        bool held = voice.Tick < voice.Length;
        voice.Released |= !held;
        double attackStep = voice.Level >= 1 ? 0 : 1.0 / Math.Max(1.0, Milliseconds(region.Attack, 1.4, 8) / 1000.0 * SampleRate);
        double sustain = Math.Pow(region.Sustain / 127.0, 2);
        double decayStep = 1.0 / Math.Max(1.0, Milliseconds(region.Decay, 15, 30) / 1000.0 * SampleRate);
        double releaseStep = 1.0 / Math.Max(1.0, Milliseconds(region.Release, 15, 30) / 1000.0 * SampleRate);
        if (voice.Released)
        {
            voice.Level -= releaseStep;
            if (voice.Level <= 0)
            {
                voice.Done = true;
                return 0;
            }
        }
        else if (!voice.Decaying)
        {
            voice.Level = region.Attack >= 127 ? 1 : Math.Min(1.0, voice.Level + attackStep);
            if (voice.Level >= 1.0) voice.Decaying = true;
        }
        else
        {
            voice.Level = Math.Max(sustain, voice.Level - decayStep);
        }

        double volume = Math.Pow(track.Volume / 127.0, 2) * Math.Pow(track.Expression / 127.0, 2) * Math.Pow(track.MainVolume / 127.0, 2) * voice.Velocity * voice.Velocity * voice.Level;
        double sampleValue;
        if (voice.Wave is { } wave)
        {
            voice.Increment = Math.Pow(2, pitch / 12.0) * wave.Rate / SampleRate;
            int length = wave.Samples.Length;
            if (voice.Position >= length)
            {
                if (wave.Loops && length > wave.LoopStart)
                {
                    voice.Position = wave.LoopStart + (voice.Position - wave.LoopStart) % (length - wave.LoopStart);
                }
                else
                {
                    voice.Done = true;
                    return 0;
                }
            }

            int index = (int)voice.Position;
            double fraction = voice.Position - index;
            double a = wave.Samples[index], b = index + 1 < length ? wave.Samples[index + 1] : wave.Loops && length > wave.LoopStart ? wave.Samples[wave.LoopStart] : a;
            sampleValue = (a + (b - a) * fraction) / 32768.0;
            voice.Position += voice.Increment;
        }
        else if (region.Type == 2)
        {
            double frequency = 440.0 * Math.Pow(2, (voice.BasePitch + track.Bend * track.BendRange / 128.0 - 69) / 12.0);
            voice.Phase = (voice.Phase + frequency / SampleRate) % 1.0;
            double duty = (Math.Clamp(region.Wave, 0, 6) + 1) / 8.0;
            sampleValue = voice.Phase < duty ? 0.5 : -0.5;
        }
        else
        {
            // Noise: a shift register clocked at the note's pitch.
            double frequency = 440.0 * Math.Pow(2, (voice.BasePitch - 69) / 12.0) * 16;
            voice.Phase += frequency / SampleRate;
            while (voice.Phase >= 1.0)
            {
                voice.Phase -= 1.0;
                uint bit = (voice.Noise ^ (voice.Noise >> 1)) & 1;
                voice.Noise = (voice.Noise >> 1) | (bit << 14);
            }

            sampleValue = (voice.Noise & 1) != 0 ? 0.4 : -0.4;
        }

        return sampleValue * volume;
    }

    #endregion
}
