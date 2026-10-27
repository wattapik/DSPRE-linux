using System.Buffers.Binary;
using System.Text;

namespace DSPRE.Core;

/// <summary>A map's scrolling-texture animation (an NSBTA, "SRT0" block): per named track, how far the
/// texture is shifted at each frame. The game runs these at 60 frames a second and loops them.</summary>
public sealed class TextureAnimation
{
    /// <summary>One track's shift in texture-coordinate units (1 = a whole texture) per frame; a
    /// constant track has a single value.</summary>
    public sealed record Track(string Name, float[] U, float[] V)
    {
        public (float U, float V) At(int frame) => (Sample(U, frame), Sample(V, frame));

        private static float Sample(float[] values, int frame) => values.Length == 0 ? 0f : values[Math.Clamp(frame, 0, values.Length - 1)];
    }

    public const float FramesPerSecond = 60f;

    public required string Name { get; init; }
    public required int FrameCount { get; init; }
    public required IReadOnlyList<Track> Tracks { get; init; }

    private const int ChannelCount = 5;
    private const int TranslationU = 3, TranslationV = 4;
    private const byte FlagConstant = 0x30, FlagSamples = 0x10;
    private const float FixedPointOne = 4096f;

    public static TextureAnimation Parse(byte[] data)
    {
        if (data.Length < 0x18 || Encoding.ASCII.GetString(data, 0, 4) != "BTA0") throw new InvalidDataException("Not an NSBTA (BTA0) file.");
        int srt = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x10));
        if (srt + 8 > data.Length || Encoding.ASCII.GetString(data, srt, 4) != "SRT0") throw new InvalidDataException("The NSBTA file has no SRT0 block.");

        (List<int> offsets, List<string> names, _) = ReadInfoBlock(data, srt + 8, 4);
        if (offsets.Count == 0) throw new InvalidDataException("The NSBTA file holds no animation.");

        int animation = srt + offsets[0];
        int frameCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(animation + 4));
        (_, List<string> trackNames, int trackData) = ReadInfoBlock(data, animation + 8, ChannelCount * 8);

        var tracks = new List<Track>();
        for (int t = 0; t < trackNames.Count; t++)
        {
            float[] u = [], v = [];
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                int at = trackData + (t * ChannelCount + channel) * 8;
                int samples = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
                byte flags = data[at + 3];
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
                float[] translation = [];
                if (flags == FlagConstant) translation = [(int)value / FixedPointOne];
                else if (flags == FlagSamples)
                {
                    translation = new float[samples];
                    for (int i = 0; i < samples; i++) translation[i] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(animation + (int)value + 2 * i)) / FixedPointOne;
                }

                if (channel == TranslationU) u = translation;
                else if (channel == TranslationV) v = translation;
            }

            tracks.Add(new Track(trackNames[t], u, v));
        }

        return new TextureAnimation { Name = names[0], FrameCount = frameCount, Tracks = tracks };
    }

    // The "info block" Nitro files use for a list of named records: a small header, a search tree this
    // reader has no use for, then the records, then their 16-byte names.
    private static (List<int> Offsets, List<string> Names, int DataStart) ReadInfoBlock(byte[] data, int start, int recordSize)
    {
        int count = data[start + 1];
        int records = start + 4 + 8 + 4 * count + 4;
        // The first word of a record is its offset when the records are just offsets (a list of animations).
        var offsets = Enumerable.Range(0, count).Select(i => (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(records + recordSize * i))).ToList();
        int names = records + count * recordSize;
        var nameList = Enumerable.Range(0, count).Select(i => Encoding.ASCII.GetString(data, names + 16 * i, 16).TrimEnd('\0', ' ')).ToList();
        return (offsets, nameList, records);
    }
}
