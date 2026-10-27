using System.Buffers.Binary;
using System.Text;

namespace DSPRE.Core;

/// <summary>Reads a Nitro instrument bank (SBNK) and the wave archives (SWAR) it uses, and plays an instrument's sample as mono
/// 16-bit audio: PCM8, PCM16 and ADPCM samples, at the pitch a key asks for. There is no sequencer, as Pokémon cries need none.</summary>
public sealed class NitroSynth
{
    private sealed class Wave
    {
        public int Type;
        public bool Loops;
        public int Rate;
        public int LoopStart, LoopLength;
        public short[] Samples = [];
    }

    private sealed record Region(int Type, int Wave, int WaveArchive, int Note, int Attack, int Decay, int Sustain, int Release);

    private sealed class Instrument
    {
        public List<(int Low, int High, Region Region)> Regions = [];
    }

    private readonly List<Instrument?> instruments = [];
    private readonly Dictionary<int, List<Wave>> waveArchives = [];
    private readonly int[] bankArchives;

    public const int SampleRate = 32768;
    public const int MaxSeconds = 8;

    public NitroSynth(byte[] bank, int[] archiveIds, Func<int, byte[]> archiveFile)
    {
        bankArchives = archiveIds;
        ReadBank(bank);
        for (int slot = 0; slot < archiveIds.Length; slot++)
        {
            if (archiveIds[slot] is >= 0 and < 0xFFFF && !waveArchives.ContainsKey(slot)) waveArchives[slot] = ReadWaveArchive(archiveFile(archiveIds[slot]));
        }
    }

    #region Banks and waves

    private void ReadBank(byte[] file)
    {
        if (file.Length < 0x3C || Encoding.ASCII.GetString(file, 0, 4) != "SBNK") throw new InvalidDataException("Not an instrument bank (SBNK).");
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x38));
        for (int i = 0; i < count; i++)
        {
            int at = 0x3C + 4 * i;
            int type = file[at];
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 1));
            if (type == 0 || offset == 0 || offset >= file.Length)
            {
                instruments.Add(null);
                continue;
            }

            var instrument = new Instrument();
            Region RegionAt(int regionType, int p) => new(regionType, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(p)), BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(p + 2)), file[p + 4], file[p + 5], file[p + 6], file[p + 7], file[p + 8]);
            if (type is >= 1 and <= 5)
            {
                instrument.Regions.Add((0, 127, RegionAt(type, offset)));
            }
            else if (type == 16)
            {
                int low = file[offset], high = file[offset + 1];
                for (int note = low; note <= high; note++)
                {
                    int p = offset + 2 + (note - low) * 12;
                    instrument.Regions.Add((note, note, RegionAt(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(p)), p + 2)));
                }
            }
            else if (type == 17)
            {
                int regions = 0;
                for (; regions < 8 && file[offset + regions] != 0; regions++) { }
                int low = 0;
                for (int r = 0; r < regions; r++)
                {
                    int high = file[offset + r];
                    int p = offset + 8 + r * 12;
                    instrument.Regions.Add((low, high, RegionAt(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(p)), p + 2)));
                    low = high + 1;
                }
            }

            instruments.Add(instrument);
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
                Rate = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 2)),
                LoopStart = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 6)) * 4,
                LoopLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 8)) * 4
            };
            int dataStart = at + 12, dataLength = Math.Min(wave.LoopStart + wave.LoopLength, file.Length - dataStart);
            wave.Samples = wave.Type switch
            {
                0 => Enumerable.Range(0, dataLength).Select(k => (short)((sbyte)file[dataStart + k] << 8)).ToArray(),
                1 => Enumerable.Range(0, dataLength / 2).Select(k => BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(dataStart + 2 * k))).ToArray(),
                _ => DecodeAdpcm(file.AsSpan(dataStart, Math.Max(dataLength, 0)))
            };

            // Loop points are counted in bytes of the stored data; convert them to samples.
            if (wave.Type == 0) { }
            else if (wave.Type == 1) { wave.LoopStart /= 2; wave.LoopLength /= 2; }
            else { wave.LoopStart = Math.Max(wave.LoopStart - 4, 0) * 2; wave.LoopLength *= 2; }
            waves.Add(wave);
        }

        return waves;
    }

    internal static readonly int[] StepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
        157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552,
        1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487,
        12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
    ];

    internal static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8];

    internal static short[] DecodeAdpcm(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return [];
        int predictor = BinaryPrimitives.ReadInt16LittleEndian(data);
        int index = Math.Clamp((int)data[2], 0, 88);
        var samples = new List<short>((data.Length - 4) * 2);
        for (int i = 4; i < data.Length; i++)
        {
            for (int half = 0; half < 2; half++)
            {
                int nibble = half == 0 ? data[i] & 0xF : data[i] >> 4;
                int step = StepTable[index];
                int difference = step >> 3;
                if ((nibble & 1) != 0) difference += step >> 2;
                if ((nibble & 2) != 0) difference += step >> 1;
                if ((nibble & 4) != 0) difference += step;
                predictor = Math.Clamp(predictor + ((nibble & 8) != 0 ? -difference : difference), short.MinValue, short.MaxValue);
                index = Math.Clamp(index + IndexTable[nibble & 7], 0, 88);
                samples.Add((short)predictor);
            }
        }

        return samples.ToArray();
    }

    #endregion

    /// <summary>Encodes 16-bit samples as the DS's ADPCM: a four-byte header (the first sample and the step index), then four bits a sample.
    /// The result is padded to whole 32-bit words.</summary>
    public static byte[] EncodeAdpcm(short[] samples)
    {
        int predictor = samples.Length > 0 ? samples[0] : 0, index = 0;
        int bytes = 4 + (samples.Length + 1) / 2;
        var output = new byte[(bytes + 3) / 4 * 4];
        BinaryPrimitives.WriteInt16LittleEndian(output, (short)predictor);
        for (int i = 0; i < samples.Length; i++)
        {
            int difference = samples[i] - predictor, nibble = 0;
            if (difference < 0)
            {
                nibble = 8;
                difference = -difference;
            }

            int step = StepTable[index];
            if (difference >= step) { nibble |= 4; difference -= step; }
            step >>= 1;
            if (difference >= step) { nibble |= 2; difference -= step; }
            step >>= 1;
            if (difference >= step) nibble |= 1;

            // Follow the decoder exactly, so the error does not build up.
            step = StepTable[index];
            int delta = step >> 3;
            if ((nibble & 1) != 0) delta += step >> 2;
            if ((nibble & 2) != 0) delta += step >> 1;
            if ((nibble & 4) != 0) delta += step;
            predictor = Math.Clamp(predictor + ((nibble & 8) != 0 ? -delta : delta), short.MinValue, short.MaxValue);
            index = Math.Clamp(index + IndexTable[nibble & 7], 0, 88);
            int at = 4 + i / 2;
            output[at] |= (byte)(i % 2 == 0 ? nibble : nibble << 4);
        }

        return output;
    }

    #region Playing

    /// <summary>Which wave archive slot (of the bank's four) and which wave in it the instrument plays for the key.</summary>
    public (int Slot, int Wave, int Rate)? FindWave(int program, int key)
    {
        Instrument? instrument = program >= 0 && program < instruments.Count ? instruments[program] : null;
        Region? region = instrument?.Regions.Where(entry => key >= entry.Low && key <= entry.High).Select(entry => entry.Region).FirstOrDefault();
        if (region is null || !waveArchives.TryGetValue(region.WaveArchive, out List<Wave>? waves) || region.Wave >= waves.Count) return null;
        return (region.WaveArchive, region.Wave, waves[region.Wave].Rate);
    }

    /// <summary>Plays one note of the bank's instrument for the key, shaped by the instrument's envelope (attack, decay, sustain, release)
    /// and ended when the sample does (up to <see cref="MaxSeconds"/>).</summary>
    /// <param name="holdSeconds">How long the note is held before the envelope releases it; 0 holds it to the end of the sample.</param>
    public short[] PlayWholeSample(int program, int key, double holdSeconds = 0)
    {
        Instrument? instrument = program >= 0 && program < instruments.Count ? instruments[program] : null;
        Region? region = instrument?.Regions.Where(entry => key >= entry.Low && key <= entry.High).Select(entry => entry.Region).FirstOrDefault();
        if (region is null || region.WaveArchive >= bankArchives.Length || !waveArchives.TryGetValue(region.WaveArchive, out List<Wave>? waves) || region.Wave >= waves.Count) return [];
        Wave wave = waves[region.Wave];
        if (wave.Samples.Length == 0) return [];

        double increment = Math.Pow(2, (key - region.Note) / 12.0) * wave.Rate / SampleRate;
        int count = (int)Math.Min(wave.Samples.Length / increment, MaxSeconds * SampleRate);
        int hold = holdSeconds > 0 ? (int)(holdSeconds * SampleRate) : int.MaxValue;
        double attackStep = region.Attack >= 109 ? 1.0 : 1.0 / (1 + (109 - region.Attack) * 8);
        double sustain = Math.Pow(region.Sustain / 127.0, 2);
        double decayStep = region.Decay >= 126 ? 0.002 : Math.Max(0.00001, (127 - region.Decay) * 0.000012);
        double releaseStep = region.Release >= 126 ? 0.01 : Math.Max(0.000015, (127 - region.Release) * 0.00004);
        double level = 0;
        bool decaying = false;

        var output = new List<short>(count);
        for (int i = 0; i < count; i++)
        {
            if (i >= hold)
            {
                level -= releaseStep;
                if (level <= 0) break;
            }
            else if (!decaying)
            {
                level += attackStep;
                if (level >= 1) { level = 1; decaying = true; }
            }
            else
            {
                level = Math.Max(sustain, level - decayStep);
            }

            double position = i * increment;
            int index = (int)position;
            double a = wave.Samples[index], b = index + 1 < wave.Samples.Length ? wave.Samples[index + 1] : a;
            output.Add((short)((a + (b - a) * (position - index)) * level));
        }

        return output.ToArray();
    }

    #endregion
}
