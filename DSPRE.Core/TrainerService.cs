using System.Buffers.Binary;

namespace DSPRE.Core;

/// <summary>One trainer's single party member. Field-for-field port of the legacy PartyPokemon
/// class (DS_Map/ROMFiles/TrainerFile.cs), keeping the raw gender/ability flags byte rather than
/// booleans so any bit combination present in existing ROM data round-trips exactly.</summary>
public sealed class PartyPokemonRecord
{
    [Flags]
    public enum GenderAbilityFlags : byte
    {
        None = 0,
        ForceMale = 0x1,
        ForceFemale = 0x2,
        AbilitySlot1 = 0x10,
        AbilitySlot2 = 0x20
    }

    public const int MonNumberBits = 10;
    public const int MonNumberMask = (1 << MonNumberBits) - 1;
    public const int MonFormMask = ((1 << 6) - 1) << MonNumberBits;

    public int? Species { get; set; }
    /// <summary>Alt form index. Unused (always 0) in Diamond/Pearl.</summary>
    public int Form { get; set; }
    public int Level { get; set; }
    /// <summary>The raw "DV" byte (0-255). IV = Difficulty*31/255; also seeds the PID/nature via the
    /// same LCG the game uses, matching DS_Map's DV Calculator - not reproduced here yet.</summary>
    public byte Difficulty { get; set; }
    public GenderAbilityFlags GenderAndAbility { get; set; }
    /// <summary>Ball capsule seal config. Diamond/Pearl never serializes this field at all.</summary>
    public ushort BallSeals { get; set; }
    public int? HeldItem { get; set; }
    /// <summary>Exactly 4 entries when the trainer has custom moves, otherwise null (moves are
    /// computed from the level-up learnset in that case).</summary>
    public int[]? Moves { get; set; }

    public bool IsEmpty => Species is null || Level <= 0;
}

/// <summary>One trainer's fixed properties. Field-for-field port of the legacy TrainerProperties
/// class.</summary>
public sealed class TrainerRecord
{
    public const int PartySize = 6;
    public const int AiFlagCount = 11;
    public const int TrainerItemCount = 4;

    public int Id { get; set; }
    /// <summary>Simple display name with the "{TRAINER_NAME:...}" text-archive wrapper already
    /// stripped - see TrainerService.UnwrapTrainerName/WrapTrainerName.</summary>
    public string Name { get; set; } = "";
    public byte TrDataUnknown { get; set; }
    public byte TrainerClass { get; set; }
    public byte PartyCount { get; set; }
    public bool DoubleBattle { get; set; }
    public bool ChooseMoves { get; set; }
    public bool ChooseItems { get; set; }
    public int[] TrainerItems { get; } = new int[TrainerItemCount];
    public bool[] Ai { get; } = new bool[AiFlagCount];
    public PartyPokemonRecord[] Party { get; } = CreateEmptyParty();

    private static PartyPokemonRecord[] CreateEmptyParty()
    {
        var party = new PartyPokemonRecord[PartySize];
        for (int i = 0; i < PartySize; i++) party[i] = new PartyPokemonRecord();
        return party;
    }
}

public static class TrainerService
{
    public static IReadOnlyList<int> GetIds(RomProject project)
    {
        if (!Directory.Exists(project.TrainerPropertiesDirectory)) return [];
        return Directory.GetFiles(project.TrainerPropertiesDirectory)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => int.TryParse(name, out _))
            .Select(int.Parse)
            .OrderBy(id => id)
            .ToArray();
    }

    /// <summary>Ball seals are only ever written/read on Platinum and HeartGold/SoulSilver - Diamond
    /// and Pearl never serialize that field, per the legacy PartyPokemon.ToByteArray/Party reader.</summary>
    public static bool UsesBallSeals(RomGameMetadata metadata) =>
        metadata.HeaderFormat is MapHeaderFormat.Platinum or MapHeaderFormat.HeartGoldSoulSilver;

    public static TrainerRecord Load(RomProject project, int id, RomGameMetadata metadata, string name)
    {
        var record = new TrainerRecord { Id = id, Name = name };

        string propertiesPath = Path.Combine(project.TrainerPropertiesDirectory, $"{id:D4}");
        byte[] properties = File.ReadAllBytes(propertiesPath);
        int offset = 0;
        byte flags = properties[offset++];
        record.ChooseMoves = (flags & 1) != 0;
        record.ChooseItems = (flags & 2) != 0;
        record.TrainerClass = properties[offset++];
        record.TrDataUnknown = properties[offset++];
        record.PartyCount = properties[offset++];
        for (int i = 0; i < TrainerRecord.TrainerItemCount; i++)
        {
            record.TrainerItems[i] = BinaryPrimitives.ReadUInt16LittleEndian(properties.AsSpan(offset, 2));
            offset += 2;
        }

        uint aiFlags = BinaryPrimitives.ReadUInt32LittleEndian(properties.AsSpan(offset, 4));
        offset += 4;
        for (int i = 0; i < TrainerRecord.AiFlagCount; i++)
        {
            record.Ai[i] = (aiFlags & (1u << i)) != 0;
        }

        uint doubleBattleFlag = BinaryPrimitives.ReadUInt32LittleEndian(properties.AsSpan(offset, 4));
        record.DoubleBattle = doubleBattleFlag == 2;

        string partyPath = Path.Combine(project.TrainerPartyDirectory, $"{id:D4}");
        bool usesBallSeals = UsesBallSeals(metadata);
        if (File.Exists(partyPath))
        {
            ReadParty(record, File.ReadAllBytes(partyPath), usesBallSeals);
        }

        return record;
    }

    private static void ReadParty(TrainerRecord record, byte[] data, bool usesBallSeals)
    {
        // Matches the legacy Party reader's cap exactly: due to operator precedence in the original
        // C# ("partyData.Length - 1 / dividend"), "1 / dividend" is always 0 for any real dividend, so
        // the cap is effectively Math.Min(data.Length, partyCount) rather than a byte-size-derived
        // count. It is preserved here for parity even though it rarely matters in practice.
        int count = Math.Min(data.Length, record.PartyCount);
        int offset = 0;
        for (int i = 0; i < count && i < TrainerRecord.PartySize; i++)
        {
            if (offset + 6 > data.Length) break;
            var poke = new PartyPokemonRecord
            {
                Difficulty = data[offset],
                GenderAndAbility = (PartyPokemonRecord.GenderAbilityFlags)data[offset + 1]
            };
            offset += 2;
            poke.Level = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
            offset += 2;
            ushort monFull = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
            offset += 2;
            poke.Species = monFull & PartyPokemonRecord.MonNumberMask;
            poke.Form = (monFull & PartyPokemonRecord.MonFormMask) >> PartyPokemonRecord.MonNumberBits;

            if (record.ChooseItems)
            {
                if (offset + 2 > data.Length) break;
                poke.HeldItem = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
                offset += 2;
            }

            if (record.ChooseMoves)
            {
                if (offset + 8 > data.Length) break;
                var moves = new int[4];
                for (int m = 0; m < 4; m++)
                {
                    ushort value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
                    moves[m] = value == ushort.MaxValue ? 0 : value;
                    offset += 2;
                }
                poke.Moves = moves;
            }

            if (usesBallSeals)
            {
                if (offset + 2 > data.Length) break;
                poke.BallSeals = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
                offset += 2;
            }

            record.Party[i] = poke;
        }
    }

    public static void Save(RomProject project, TrainerRecord record, RomGameMetadata metadata) =>
        EditJournal.Track(project, EditJournalDocuments.Trainer, EditJournalDocuments.Keys(("trainer", record.Id)), () => SaveUntracked(project, record, metadata));

    private static void SaveUntracked(RomProject project, TrainerRecord record, RomGameMetadata metadata)
    {
        Directory.CreateDirectory(project.TrainerPropertiesDirectory);
        Directory.CreateDirectory(project.TrainerPartyDirectory);

        using (var properties = new MemoryStream())
        {
            byte flags = 0;
            if (record.ChooseMoves) flags |= 1;
            if (record.ChooseItems) flags |= 2;
            properties.WriteByte(flags);
            properties.WriteByte(record.TrainerClass);
            properties.WriteByte(record.TrDataUnknown);
            properties.WriteByte(record.PartyCount);
            foreach (int item in record.TrainerItems)
            {
                WriteUInt16(properties, (ushort)item);
            }

            uint aiFlags = 0;
            for (int i = 0; i < TrainerRecord.AiFlagCount; i++)
            {
                if (record.Ai[i]) aiFlags |= 1u << i;
            }
            WriteUInt32(properties, aiFlags);
            WriteUInt32(properties, record.DoubleBattle ? 2u : 0u);
            File.WriteAllBytes(Path.Combine(project.TrainerPropertiesDirectory, $"{record.Id:D4}"), properties.ToArray());
        }

        bool usesBallSeals = UsesBallSeals(metadata);
        using (var party = new MemoryStream())
        {
            foreach (PartyPokemonRecord poke in record.Party)
            {
                if (poke.IsEmpty) continue;

                party.WriteByte(poke.Difficulty);
                party.WriteByte((byte)poke.GenderAndAbility);
                WriteUInt16(party, (ushort)poke.Level);
                ushort packed = (ushort)((poke.Species ?? 0) | (poke.Form << PartyPokemonRecord.MonNumberBits));
                WriteUInt16(party, packed);

                if (record.ChooseItems)
                {
                    WriteUInt16(party, (ushort)(poke.HeldItem ?? 0));
                }

                if (record.ChooseMoves)
                {
                    int[] moves = poke.Moves ?? new int[4];
                    for (int m = 0; m < 4; m++)
                    {
                        WriteUInt16(party, (ushort)(m < moves.Length ? moves[m] : 0));
                    }
                }

                if (usesBallSeals)
                {
                    WriteUInt16(party, poke.BallSeals);
                }
            }

            File.WriteAllBytes(Path.Combine(project.TrainerPartyDirectory, $"{record.Id:D4}"), party.ToArray());
        }
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    // Trainer/trainer-class simple names are stored wrapped as "{TRAINER_NAME:...}" inside their text
    // archive message, matching DS_Map's TextConverter.GetSimpleTrainerName/ReplaceTrainerName.
    public static string UnwrapTrainerName(string message) =>
        message.StartsWith("{TRAINER_NAME:", StringComparison.Ordinal) && message.EndsWith('}')
            ? message[14..^1]
            : message;

    public static string WrapTrainerName(string simpleName) => $"{{TRAINER_NAME:{simpleName}}}";

    public static int GetTrainerNamesArchiveId(RomGameMetadata metadata) => metadata.HeaderFormat switch
    {
        MapHeaderFormat.DiamondPearl => metadata.Language == RomLanguage.Japanese ? 550 : 559,
        MapHeaderFormat.Platinum => 618,
        MapHeaderFormat.HeartGoldSoulSilver => metadata.Language == RomLanguage.Japanese ? 719 : 729,
        _ => 559
    };

    /// <summary>The trainer an overworld event of type Trainer battles, from its script number -
    /// the inverse of how the legacy Event Editor assigns one (DS_Map/Editors/EventEditor.cs,
    /// NavigateToOverworldTarget): script 2999 + n for a normal trainer, 4999 + n for a double battle's
    /// partner, with one script number per game skipped. Null for a script that is not a trainer's.</summary>
    public static int? GetTrainerIdForOverworldScript(RomGameMetadata metadata, ushort scriptNumber)
    {
        const int trainerScriptBase = 2999;
        const int partnerScriptBase = 4999;
        if (scriptNumber <= trainerScriptBase || scriptNumber == ushort.MaxValue) return null;

        int skippedScript = metadata.HeaderFormat switch
        {
            MapHeaderFormat.DiamondPearl => 851,
            MapHeaderFormat.Platinum => 929,
            _ => 740
        };
        int trainerId = scriptNumber >= partnerScriptBase ? scriptNumber - partnerScriptBase : scriptNumber - trainerScriptBase;
        if (trainerId > skippedScript - 1) trainerId--;
        return trainerId;
    }

    public static int GetTrainerClassNamesArchiveId(RomGameMetadata metadata) => metadata.HeaderFormat switch
    {
        MapHeaderFormat.DiamondPearl => metadata.Language == RomLanguage.Japanese ? 551 : 560,
        MapHeaderFormat.Platinum => 619,
        MapHeaderFormat.HeartGoldSoulSilver => metadata.Language == RomLanguage.Japanese ? 720 : 730,
        _ => 560
    };

    public static int GetPokemonNamesArchiveId(RomGameMetadata metadata) => metadata.HeaderFormat switch
    {
        MapHeaderFormat.DiamondPearl => 362,
        MapHeaderFormat.Platinum => 412,
        MapHeaderFormat.HeartGoldSoulSilver => metadata.Language == RomLanguage.Japanese ? 232 : 237,
        _ => 412
    };

    public static int GetAbilityNamesArchiveId(RomGameMetadata metadata) => metadata.HeaderFormat switch
    {
        MapHeaderFormat.DiamondPearl => 552,
        MapHeaderFormat.Platinum => 610,
        MapHeaderFormat.HeartGoldSoulSilver => 720,
        _ => 610
    };

    public static int GetAttackNamesArchiveId(RomGameMetadata metadata) => metadata.HeaderFormat switch
    {
        MapHeaderFormat.DiamondPearl => 588,
        MapHeaderFormat.Platinum => 647,
        MapHeaderFormat.HeartGoldSoulSilver => metadata.Language == RomLanguage.Japanese ? 739 : 750,
        _ => 647
    };

    public static int GetItemNamesArchiveId(RomGameMetadata metadata) => metadata.HeaderFormat switch
    {
        MapHeaderFormat.DiamondPearl => metadata.Language == RomLanguage.Japanese ? 341 : 344,
        MapHeaderFormat.Platinum => 392,
        MapHeaderFormat.HeartGoldSoulSilver => metadata.Language == RomLanguage.Japanese ? 219 : 222,
        _ => 392
    };
}
