using System.Buffers.Binary;

namespace DSPRE.Core;

public enum MapHeaderFormat
{
    DiamondPearl,
    Platinum,
    HeartGoldSoulSilver
}

public sealed record MapHeaderRecord
{
    public const int SerializedLength = 24;

    public required ushort Id { get; init; }
    public required MapHeaderFormat Format { get; init; }
    public byte AreaDataId { get; init; }
    public byte AreaIcon { get; init; }
    public byte Unknown0 { get; init; }
    public byte Unknown1 { get; init; }
    public ushort MatrixId { get; init; }
    public ushort ScriptFileId { get; init; }
    public ushort LevelScriptId { get; init; }
    public ushort TextArchiveId { get; init; }
    public ushort MusicDayId { get; init; }
    public ushort MusicNightId { get; init; }
    public ushort WildPokemonId { get; init; }
    public ushort EventFileId { get; init; }
    public ushort LocationNameId { get; init; }
    public byte WeatherId { get; init; }
    public byte CameraAngleId { get; init; }
    public byte LocationSpecifier { get; init; }
    public byte BattleBackground { get; init; }
    public byte Flags { get; init; }
    public byte FollowMode { get; init; }
    public byte LocationType { get; init; }
    public byte WorldMapX { get; init; }
    public byte WorldMapY { get; init; }
    public bool KantoFlag { get; init; }

    public static MapHeaderRecord Parse(ReadOnlySpan<byte> data, ushort id, MapHeaderFormat format)
    {
        if (data.Length < SerializedLength)
        {
            throw new InvalidDataException($"Header {id} contains {data.Length} bytes; expected {SerializedLength}.");
        }

        return format switch
        {
            MapHeaderFormat.DiamondPearl => ParseDiamondPearl(data, id),
            MapHeaderFormat.Platinum => ParsePlatinum(data, id),
            MapHeaderFormat.HeartGoldSoulSilver => ParseHeartGoldSoulSilver(data, id),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
    }

    public byte[] Serialize()
    {
        byte[] data = new byte[SerializedLength];
        Span<byte> output = data;

        switch (Format)
        {
            case MapHeaderFormat.DiamondPearl:
                output[0] = AreaDataId;
                output[1] = Unknown1;
                WriteCommon(output);
                BinaryPrimitives.WriteUInt16LittleEndian(output[18..], LocationNameId);
                output[20] = WeatherId;
                output[21] = CameraAngleId;
                output[22] = LocationSpecifier;
                output[23] = (byte)((BattleBackground & 0x0F) | ((Flags & 0x0F) << 4));
                break;
            case MapHeaderFormat.Platinum:
                output[0] = AreaDataId;
                output[1] = Unknown1;
                WriteCommon(output);
                output[18] = (byte)LocationNameId;
                output[19] = AreaIcon;
                output[20] = WeatherId;
                output[21] = CameraAngleId;
                BinaryPrimitives.WriteUInt16LittleEndian(output[22..], (ushort)(
                    (LocationSpecifier & 0x7F) |
                    ((BattleBackground & 0x1F) << 7) |
                    ((Flags & 0x0F) << 12)));
                break;
            case MapHeaderFormat.HeartGoldSoulSilver:
                output[0] = (byte)WildPokemonId;
                output[1] = AreaDataId;
                BinaryPrimitives.WriteUInt16LittleEndian(output[2..], (ushort)(
                    (Unknown0 & 0x0F) |
                    ((WorldMapX & 0x3F) << 4) |
                    ((WorldMapY & 0x3F) << 10)));
                WriteHeartGoldSoulSilverCommon(output);
                output[18] = (byte)LocationNameId;
                output[19] = (byte)((AreaIcon & 0x0F) | ((Unknown1 & 0x0F) << 4));
                BinaryPrimitives.WriteUInt32LittleEndian(output[20..], BuildHeartGoldSoulSilverFlags());
                break;
            default:
                throw new InvalidOperationException($"Unsupported header format {Format}.");
        }

        return data;
    }

    private void WriteCommon(Span<byte> output, int offset = 2)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(output[offset..], MatrixId);
        BinaryPrimitives.WriteUInt16LittleEndian(output[(offset + 2)..], ScriptFileId);
        BinaryPrimitives.WriteUInt16LittleEndian(output[(offset + 4)..], LevelScriptId);
        BinaryPrimitives.WriteUInt16LittleEndian(output[(offset + 6)..], TextArchiveId);
        BinaryPrimitives.WriteUInt16LittleEndian(output[(offset + 8)..], MusicDayId);
        BinaryPrimitives.WriteUInt16LittleEndian(output[(offset + 10)..], MusicNightId);
        BinaryPrimitives.WriteUInt16LittleEndian(output[(offset + 12)..], WildPokemonId);
        BinaryPrimitives.WriteUInt16LittleEndian(output[(offset + 14)..], EventFileId);
    }

    private void WriteHeartGoldSoulSilverCommon(Span<byte> output)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(output[4..], MatrixId);
        BinaryPrimitives.WriteUInt16LittleEndian(output[6..], ScriptFileId);
        BinaryPrimitives.WriteUInt16LittleEndian(output[8..], LevelScriptId);
        BinaryPrimitives.WriteUInt16LittleEndian(output[10..], TextArchiveId);
        BinaryPrimitives.WriteUInt16LittleEndian(output[12..], MusicDayId);
        BinaryPrimitives.WriteUInt16LittleEndian(output[14..], MusicNightId);
        BinaryPrimitives.WriteUInt16LittleEndian(output[16..], EventFileId);
    }

    private uint BuildHeartGoldSoulSilverFlags()
    {
        uint value = (uint)((WeatherId & 0x7F) << 1);
        value |= (uint)((LocationType & 0x0F) << 8);
        value |= (uint)((CameraAngleId & 0x3F) << 12);
        value |= (uint)((FollowMode & 0x03) << 18);
        value |= (uint)((BattleBackground & 0x1F) << 20);
        value |= (uint)((Flags & 0x7F) << 25);
        return KantoFlag ? value | 1u : value;
    }

    private static MapHeaderRecord ParseDiamondPearl(ReadOnlySpan<byte> data, ushort id)
    {
        return new MapHeaderRecord
        {
            Id = id,
            Format = MapHeaderFormat.DiamondPearl,
            AreaDataId = data[0],
            Unknown1 = data[1],
            MatrixId = ReadUInt16(data, 2),
            ScriptFileId = ReadUInt16(data, 4),
            LevelScriptId = ReadUInt16(data, 6),
            TextArchiveId = ReadUInt16(data, 8),
            MusicDayId = ReadUInt16(data, 10),
            MusicNightId = ReadUInt16(data, 12),
            WildPokemonId = ReadUInt16(data, 14),
            EventFileId = ReadUInt16(data, 16),
            LocationNameId = ReadUInt16(data, 18),
            WeatherId = data[20],
            CameraAngleId = data[21],
            LocationSpecifier = data[22],
            BattleBackground = (byte)(data[23] & 0x0F),
            Flags = (byte)(data[23] >> 4)
        };
    }

    private static MapHeaderRecord ParsePlatinum(ReadOnlySpan<byte> data, ushort id)
    {
        ushort settings = ReadUInt16(data, 22);
        return new MapHeaderRecord
        {
            Id = id,
            Format = MapHeaderFormat.Platinum,
            AreaDataId = data[0],
            Unknown1 = data[1],
            MatrixId = ReadUInt16(data, 2),
            ScriptFileId = ReadUInt16(data, 4),
            LevelScriptId = ReadUInt16(data, 6),
            TextArchiveId = ReadUInt16(data, 8),
            MusicDayId = ReadUInt16(data, 10),
            MusicNightId = ReadUInt16(data, 12),
            WildPokemonId = ReadUInt16(data, 14),
            EventFileId = ReadUInt16(data, 16),
            LocationNameId = data[18],
            AreaIcon = data[19],
            WeatherId = data[20],
            CameraAngleId = data[21],
            LocationSpecifier = (byte)(settings & 0x7F),
            BattleBackground = (byte)((settings >> 7) & 0x1F),
            Flags = (byte)((settings >> 12) & 0x0F)
        };
    }

    private static MapHeaderRecord ParseHeartGoldSoulSilver(ReadOnlySpan<byte> data, ushort id)
    {
        ushort coordinates = ReadUInt16(data, 2);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        return new MapHeaderRecord
        {
            Id = id,
            Format = MapHeaderFormat.HeartGoldSoulSilver,
            WildPokemonId = data[0],
            AreaDataId = data[1],
            Unknown0 = (byte)(coordinates & 0x0F),
            WorldMapX = (byte)((coordinates >> 4) & 0x3F),
            WorldMapY = (byte)((coordinates >> 10) & 0x3F),
            MatrixId = ReadUInt16(data, 4),
            ScriptFileId = ReadUInt16(data, 6),
            LevelScriptId = ReadUInt16(data, 8),
            TextArchiveId = ReadUInt16(data, 10),
            MusicDayId = ReadUInt16(data, 12),
            MusicNightId = ReadUInt16(data, 14),
            EventFileId = ReadUInt16(data, 16),
            LocationNameId = data[18],
            AreaIcon = (byte)(data[19] & 0x0F),
            Unknown1 = (byte)(data[19] >> 4),
            KantoFlag = (flags & 1) != 0,
            WeatherId = (byte)((flags >> 1) & 0x7F),
            LocationType = (byte)((flags >> 8) & 0x0F),
            CameraAngleId = (byte)((flags >> 12) & 0x3F),
            FollowMode = (byte)((flags >> 18) & 0x03),
            BattleBackground = (byte)((flags >> 20) & 0x1F),
            Flags = (byte)((flags >> 25) & 0x7F)
        };
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    }
}
