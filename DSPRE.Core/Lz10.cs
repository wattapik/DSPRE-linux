namespace DSPRE.Core;

/// <summary>The DS BIOS's LZ77 (type 0x10) compression, as used for graphics files stored inside NARCs.</summary>
public static class Lz10
{
    public static bool IsCompressed(byte[] data) => data.Length >= 4 && data[0] == 0x10;

    public static byte[] Decompress(byte[] data)
    {
        if (!IsCompressed(data)) throw new InvalidDataException("Not LZ77-compressed data.");
        int size = data[1] | (data[2] << 8) | (data[3] << 16);
        var output = new byte[size];
        int read = 4, written = 0;
        while (written < size && read < data.Length)
        {
            byte flags = data[read++];
            for (int bit = 0; bit < 8 && written < size; bit++)
            {
                if ((flags & (0x80 >> bit)) == 0)
                {
                    output[written++] = data[read++];
                    continue;
                }

                int pair = (data[read] << 8) | data[read + 1];
                read += 2;
                int length = (pair >> 12) + 3, distance = (pair & 0xFFF) + 1;
                if (distance > written) throw new InvalidDataException("The compressed data is damaged.");
                for (int i = 0; i < length && written < size; i++, written++) output[written] = output[written - distance];
            }
        }

        return output;
    }

    /// <summary>Compresses with LZ77 type 0x10 (greedy matching of up to 18 bytes within 4096).</summary>
    public static byte[] Compress(byte[] data)
    {
        var output = new List<byte>(data.Length / 2 + 16) { 0x10, (byte)data.Length, (byte)(data.Length >> 8), (byte)(data.Length >> 16) };
        var head = new int[1 << 15];
        Array.Fill(head, -1);
        var previous = new int[data.Length];
        int HashAt(int at) => ((data[at] << 8) ^ (data[at + 1] << 4) ^ data[at + 2]) & 0x7FFF;
        void Insert(int at)
        {
            if (at + 2 >= data.Length) return;
            int h = HashAt(at);
            previous[at] = head[h];
            head[h] = at;
        }

        int position = 0;
        while (position < data.Length)
        {
            int flagsAt = output.Count;
            output.Add(0);
            for (int bit = 0; bit < 8 && position < data.Length; bit++)
            {
                int bestLength = 0, bestDistance = 0;
                if (position + 2 < data.Length)
                {
                    int candidate = head[HashAt(position)], tries = 64;
                    while (candidate >= 0 && position - candidate <= 4096 && tries-- > 0)
                    {
                        int length = 0;
                        while (length < 18 && position + length < data.Length && data[candidate + length] == data[position + length]) length++;
                        if (length > bestLength)
                        {
                            bestLength = length;
                            bestDistance = position - candidate;
                            if (length == 18) break;
                        }

                        candidate = previous[candidate];
                    }
                }

                if (bestLength >= 3)
                {
                    output[flagsAt] |= (byte)(0x80 >> bit);
                    int pair = ((bestLength - 3) << 12) | (bestDistance - 1);
                    output.Add((byte)(pair >> 8));
                    output.Add((byte)pair);
                    for (int i = 0; i < bestLength; i++) Insert(position + i);
                    position += bestLength;
                }
                else
                {
                    output.Add(data[position]);
                    Insert(position);
                    position++;
                }
            }
        }

        while (output.Count % 4 != 0) output.Add(0);
        return output.ToArray();
    }
}
