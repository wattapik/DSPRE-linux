using System.Buffers.Binary;

namespace DSPRE.Core;

/// <summary>A Battle Tower / Battle Stage Pokémon set: the 16-byte record the game's rental and opponent teams are built from.</summary>
public sealed class FrontierPokemon
{
    public ushort Species { get; set; }
    public ushort[] Moves { get; } = new ushort[4];

    /// <summary>One bit per stat (HP, Attack, Defense, Speed, Sp. Atk, Sp. Def, from bit 0) that gets effort values.</summary>
    public byte EvFlags { get; set; }
    public byte Nature { get; set; }
    public ushort Item { get; set; }

    /// <summary>The record's last two bytes; 0 in nearly every set, 1 or 2 in two Battle Stage ones.</summary>
    public ushort Extra { get; set; }
}

public sealed class FrontierPokemonSets
{
    public List<FrontierPokemon> Sets { get; set; } = [];
}

/// <summary>A Battle Tower trainer: its trainer class and the sets (indexes into the Pokémon sets) it may bring.</summary>
public sealed class FrontierTrainer
{
    public ushort TrainerClass { get; set; }
    public List<ushort> SetIds { get; set; } = [];
}

public sealed class FrontierTrainers
{
    public List<FrontierTrainer> Trainers { get; set; } = [];
}

public enum FrontierFacility { Tower, Stage }

/// <summary>Platinum's Battle Tower (<c>b_pl_tower/pl_btdpm</c> sets, <c>pl_btdtr</c> trainers) and Battle Stage (<c>b_pl_stage/pl_bsdpm</c>
/// sets). Member 0 of each sets archive is an all-zero placeholder, so a set's id is its member number. A trainer is
/// <c>class u16, count u16, count x set id u16</c> padded to 4 bytes.</summary>
public static class FrontierService
{
    public const int SetSize = 16;

    private static string SetsPath(RomProject project, FrontierFacility facility) => facility == FrontierFacility.Tower
        ? Path.Combine(project.FilesPath, "battle", "b_pl_tower", "pl_btdpm.narc")
        : Path.Combine(project.FilesPath, "battle", "b_pl_stage", "pl_bsdpm.narc");

    private static string TrainersPath(RomProject project) => Path.Combine(project.FilesPath, "battle", "b_pl_tower", "pl_btdtr.narc");

    public static bool IsSupported(RomProject project, RomGameMetadata metadata) =>
        metadata.HeaderFormat == MapHeaderFormat.Platinum && File.Exists(SetsPath(project, FrontierFacility.Tower)) && File.Exists(TrainersPath(project));

    public static FrontierPokemonSets LoadSets(RomProject project, FrontierFacility facility)
    {
        string path = SetsPath(project, facility);
        var sets = new FrontierPokemonSets();
        for (int member = 0, count = NarcFile.MemberCount(path); member < count; member++)
        {
            byte[] data = NarcFile.ReadMember(path, member);
            if (data.Length != SetSize) throw new InvalidDataException($"Set {member} is {data.Length} bytes; expected {SetSize}.");
            var set = new FrontierPokemon { Species = BinaryPrimitives.ReadUInt16LittleEndian(data), EvFlags = data[10], Nature = data[11], Item = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12)), Extra = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14)) };
            for (int move = 0; move < 4; move++) set.Moves[move] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2 + move * 2));
            sets.Sets.Add(set);
        }

        return sets;
    }

    public static void SaveSets(RomProject project, FrontierFacility facility, FrontierPokemonSets sets) =>
        EditJournal.Track(project, EditJournalDocuments.FrontierSets, EditJournalDocuments.Keys(("facility", facility.ToString())), () =>
        {
            string path = SetsPath(project, facility);
            int count = NarcFile.MemberCount(path);
            if (sets.Sets.Count != count) throw new InvalidDataException($"There are {sets.Sets.Count} sets; the archive has {count}.");
            var replacements = new Dictionary<int, byte[]>();
            for (int member = 0; member < count; member++)
            {
                FrontierPokemon set = sets.Sets[member];
                var data = new byte[SetSize];
                BinaryPrimitives.WriteUInt16LittleEndian(data, set.Species);
                for (int move = 0; move < 4; move++) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2 + move * 2), set.Moves[move]);
                data[10] = set.EvFlags;
                data[11] = set.Nature;
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), set.Item);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), set.Extra);
                if (!data.AsSpan().SequenceEqual(NarcFile.ReadMember(path, member))) replacements[member] = data;
            }

            if (replacements.Count > 0) NarcFile.ReplaceMembers(path, replacements);
        });

    public static FrontierTrainers LoadTrainers(RomProject project)
    {
        string path = TrainersPath(project);
        var trainers = new FrontierTrainers();
        for (int member = 0, count = NarcFile.MemberCount(path); member < count; member++)
        {
            byte[] data = NarcFile.ReadMember(path, member);
            int ids = data.Length >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)) : 0;
            if (data.Length < 4 || data.Length < 4 + ids * 2) throw new InvalidDataException($"Trainer {member} is damaged.");
            var trainer = new FrontierTrainer { TrainerClass = BinaryPrimitives.ReadUInt16LittleEndian(data) };
            for (int i = 0; i < ids; i++) trainer.SetIds.Add(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4 + i * 2)));
            trainers.Trainers.Add(trainer);
        }

        return trainers;
    }

    public static void SaveTrainers(RomProject project, FrontierTrainers trainers) =>
        EditJournal.Track(project, EditJournalDocuments.FrontierTrainers, EditJournalDocuments.Keys(), () =>
        {
            string path = TrainersPath(project);
            int count = NarcFile.MemberCount(path);
            if (trainers.Trainers.Count != count) throw new InvalidDataException($"There are {trainers.Trainers.Count} trainers; the archive has {count}.");
            var replacements = new Dictionary<int, byte[]>();
            for (int member = 0; member < count; member++)
            {
                FrontierTrainer trainer = trainers.Trainers[member];
                if (trainer.SetIds.Count > ushort.MaxValue) throw new InvalidDataException($"Trainer {member} has too many sets.");
                var data = new byte[(4 + trainer.SetIds.Count * 2 + 3) & ~3];
                BinaryPrimitives.WriteUInt16LittleEndian(data, trainer.TrainerClass);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), (ushort)trainer.SetIds.Count);
                for (int i = 0; i < trainer.SetIds.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4 + i * 2), trainer.SetIds[i]);
                if (!data.AsSpan().SequenceEqual(NarcFile.ReadMember(path, member))) replacements[member] = data;
            }

            if (replacements.Count > 0) NarcFile.ReplaceMembers(path, replacements);
        });
}
