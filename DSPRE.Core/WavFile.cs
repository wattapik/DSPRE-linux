using System.Buffers.Binary;
using System.Text;

namespace DSPRE.Core;

/// <summary>Reads a RIFF WAV file (8, 16, 24 or 32-bit PCM, or 32-bit float) and mixes it down to mono.</summary>
public static class WavFile
{
    public sealed record Audio(float[] Samples, int SampleRate);

    public static Audio Read(byte[] data)
    {
        if (data.Length < 12 || Encoding.ASCII.GetString(data, 0, 4) != "RIFF" || Encoding.ASCII.GetString(data, 8, 4) != "WAVE") throw new InvalidDataException("Not a WAV file.");
        int format = 0, channels = 0, rate = 0, bits = 0;
        int dataStart = -1, dataLength = 0;
        for (int at = 12; at + 8 <= data.Length;)
        {
            string id = Encoding.ASCII.GetString(data, at, 4);
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4)), (uint)(data.Length - at - 8));
            if (id == "fmt ")
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 8));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 10));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 12));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 22));
                // Extensible: the real format is in the sub-format.
                if (format == 0xFFFE && size >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 32));
            }
            else if (id == "data")
            {
                dataStart = at + 8;
                dataLength = size;
            }

            at += 8 + size + (size & 1);
        }

        if (dataStart < 0 || channels < 1 || rate < 1) throw new InvalidDataException("The WAV file has no audio data.");
        int bytes = bits / 8;
        if (!(format == 1 && bits is 8 or 16 or 24 or 32) && !(format == 3 && bits == 32)) throw new InvalidDataException($"This WAV format is not supported ({bits}-bit, format {format}); use 16-bit PCM.");

        int frames = dataLength / (bytes * channels);
        var samples = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int at = dataStart + (i * channels + c) * bytes;
                sum += format == 3 ? BitConverter.ToSingle(data, at)
                    : bits == 8 ? (data[at] - 128) / 128f
                    : bits == 16 ? BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(at)) / 32768f
                    : bits == 24 ? ((data[at] << 8) | (data[at + 1] << 16) | (data[at + 2] << 24)) / 2147483648f
                    : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)) / 2147483648f;
            }

            samples[i] = sum / channels;
        }

        return new Audio(samples, rate);
    }
}
