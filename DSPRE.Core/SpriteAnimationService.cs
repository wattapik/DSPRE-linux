namespace DSPRE.Core;

/// <summary>One step of a battle sprite's frame sequence. Frame 0 and 1 are the two halves of the 160x80
/// sprite sheet, -1 ends the sequence, and anything lower is a loop: it jumps back to step
/// (-Frame - 2), repeating Duration times.</summary>
public sealed class SpriteAnimationStep
{
    public sbyte Frame { get; set; } = -1;
    /// <summary>Ticks the step lasts, minus one (the game counts it down to 0 before moving on).</summary>
    public byte Duration { get; set; }
    public sbyte XShift { get; set; }
    public sbyte YShift { get; set; }
}

/// <summary>The animation played when one side of the sprite appears: the front when the Pokémon is
/// the opponent's, the back when it is the player's.</summary>
public sealed class SpriteFaceAnimation
{
    public byte CryDelay { get; set; }
    /// <summary>Which of the game's built-in motion routines (bounces, stretches...) plays over the
    /// frames; they are code, so only the number can be chosen.</summary>
    public byte Motion { get; set; }
    public byte StartDelay { get; set; }
    public List<SpriteAnimationStep> Steps { get; set; } = [];
}

/// <summary>A species' battle sprite animation and placement.</summary>
public sealed class PokemonSpriteAnimation
{
    public SpriteFaceAnimation Front { get; set; } = new();
    public SpriteFaceAnimation Back { get; set; } = new();
    /// <summary>Extra vertical offset of the front sprite.</summary>
    public sbyte FrontYOffset { get; set; }
    public sbyte ShadowXOffset { get; set; }
    public byte ShadowSize { get; set; }
    /// <summary>Vertical position in battle, from the separate height archive: female back, male back,
    /// female front, male front. -1 where the archive has no entry, as for the missing gender of a
    /// single-gender species.</summary>
    public List<int> Heights { get; set; } = [];
}

/// <summary>Battle sprite animation data. Layout from the decompilations: pokeplatinum
/// SpeciesSpriteData (struct_defs/species_sprite_data.h, read by pokemon.c PokemonSprite_LoadAnimFrames)
/// and pokeheartgold UnkStruct_02072914 (NARC_ReadPokepicAnimScript), the same 89 bytes per species in
/// file 0 of one archive: front then back animation (cry delay, motion, start delay, 10 steps of 4 bytes),
/// then the front Y offset, shadow X offset and shadow size. Heights are one byte per member of the
/// height archive, member species * 4 + face (0 back, 2 front) + 1 for male (LoadPokemonSpriteYOffset).
/// Diamond/Pearl has no such archive.</summary>
public static class SpriteAnimationService
{
    public const int StepCount = 10;
    private const int FaceSize = 3 + StepCount * 4;
    private const int RecordSize = FaceSize * 2 + 3;

    public static bool IsSupported(RomGameMetadata metadata) => metadata.HeaderFormat != MapHeaderFormat.DiamondPearl;

    private static (string Data, string Heights) Paths(RomProject project, RomGameMetadata metadata) => metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver
        ? (Path.Combine(project.FilesPath, "a", "1", "8", "0"), Path.Combine(project.FilesPath, "a", "0", "0", "5"))
        : (Path.Combine(project.FilesPath, "poketool", "poke_edit", "pl_poke_data.narc"), Path.Combine(project.FilesPath, "poketool", "pokegra", "height.narc"));

    /// <summary>How many species have animation records.</summary>
    public static int SpeciesCount(RomProject project, RomGameMetadata metadata)
    {
        if (!IsSupported(metadata)) return 0;
        string path = Paths(project, metadata).Data;
        return File.Exists(path) ? NarcFile.ReadMember(path, 0).Length / RecordSize : 0;
    }

    public static PokemonSpriteAnimation Load(RomProject project, RomGameMetadata metadata, int species)
    {
        if (!IsSupported(metadata)) throw new InvalidOperationException("Diamond/Pearl has no battle sprite animation data.");
        (string dataPath, string heightPath) = Paths(project, metadata);
        byte[] data = NarcFile.ReadMember(dataPath, 0);
        if ((species + 1) * RecordSize > data.Length) throw new InvalidDataException($"There is no animation record for species {species}.");
        ReadOnlySpan<byte> record = data.AsSpan(species * RecordSize, RecordSize);

        var animation = new PokemonSpriteAnimation
        {
            Front = ReadFace(record[..FaceSize]),
            Back = ReadFace(record[FaceSize..(FaceSize * 2)]),
            FrontYOffset = (sbyte)record[FaceSize * 2],
            ShadowXOffset = (sbyte)record[FaceSize * 2 + 1],
            ShadowSize = record[FaceSize * 2 + 2]
        };
        for (int i = 0; i < 4; i++) animation.Heights.Add(NarcFile.ReadMember(heightPath, species * 4 + i) is { Length: > 0 } height ? height[0] : -1);
        return animation;
    }

    public static void Save(RomProject project, RomGameMetadata metadata, int species, PokemonSpriteAnimation animation) =>
        EditJournal.Track(project, EditJournalDocuments.SpriteAnimation, EditJournalDocuments.Keys(("species", species)), () => SaveUntracked(project, metadata, species, animation));

    private static void SaveUntracked(RomProject project, RomGameMetadata metadata, int species, PokemonSpriteAnimation animation)
    {
        if (animation.Front.Steps.Count != StepCount || animation.Back.Steps.Count != StepCount || animation.Heights.Count != 4)
        {
            throw new InvalidDataException($"An animation has exactly {StepCount} steps per side and 4 heights.");
        }

        (string dataPath, string heightPath) = Paths(project, metadata);
        byte[] record = new byte[RecordSize];
        WriteFace(animation.Front, record.AsSpan(0, FaceSize));
        WriteFace(animation.Back, record.AsSpan(FaceSize, FaceSize));
        record[FaceSize * 2] = (byte)animation.FrontYOffset;
        record[FaceSize * 2 + 1] = (byte)animation.ShadowXOffset;
        record[FaceSize * 2 + 2] = animation.ShadowSize;
        NarcFile.WriteMember(dataPath, 0, species * RecordSize, record);
        for (int i = 0; i < 4; i++)
        {
            // An entry the archive lacks stays missing: adding one would resize the archive.
            if (animation.Heights[i] < 0) continue;
            NarcFile.WriteMember(heightPath, species * 4 + i, 0, [(byte)Math.Clamp(animation.Heights[i], 0, 255)]);
        }
    }

    private static SpriteFaceAnimation ReadFace(ReadOnlySpan<byte> bytes)
    {
        var face = new SpriteFaceAnimation { CryDelay = bytes[0], Motion = bytes[1], StartDelay = bytes[2] };
        for (int i = 0; i < StepCount; i++)
        {
            ReadOnlySpan<byte> step = bytes.Slice(3 + i * 4, 4);
            face.Steps.Add(new SpriteAnimationStep { Frame = (sbyte)step[0], Duration = step[1], XShift = (sbyte)step[2], YShift = (sbyte)step[3] });
        }

        return face;
    }

    private static void WriteFace(SpriteFaceAnimation face, Span<byte> bytes)
    {
        bytes[0] = face.CryDelay;
        bytes[1] = face.Motion;
        bytes[2] = face.StartDelay;
        for (int i = 0; i < StepCount; i++)
        {
            SpriteAnimationStep step = face.Steps[i];
            bytes[3 + i * 4] = (byte)step.Frame;
            bytes[4 + i * 4] = step.Duration;
            bytes[5 + i * 4] = (byte)step.XShift;
            bytes[6 + i * 4] = (byte)step.YShift;
        }
    }

    /// <summary>One displayed state of the sequence: which frame, its shift, and for how many ticks.</summary>
    public sealed record PlayedFrame(int Step, int Frame, int XShift, int YShift, int Ticks);

    /// <summary>The sequence as the game plays it (pokemon_sprite.c TickPokemonSpriteAnim): each step lasts
    /// Duration + 1 ticks, loop steps jump back until their repeat count is used up, and -1 or the
    /// tenth step ends it. Capped, since a loop can be written to never end.</summary>
    public static List<PlayedFrame> Play(IReadOnlyList<SpriteAnimationStep> steps, int maxFrames = 200)
    {
        var played = new List<PlayedFrame>();
        var loopTimers = new int[steps.Count];
        int current = 0;
        // The game starts on step 0 without checking it for a loop or an end.
        if (steps.Count == 0 || steps[0].Frame < 0) return played;
        played.Add(new PlayedFrame(0, steps[0].Frame, steps[0].XShift, steps[0].YShift, steps[0].Duration + 1));
        while (played.Count < maxFrames)
        {
            current++;
            while (current < steps.Count && steps[current].Frame < -1)
            {
                loopTimers[current]++;
                if (steps[current].Duration == loopTimers[current] || steps[current].Duration == 0)
                {
                    loopTimers[current] = 0;
                    current++;
                }
                else
                {
                    current = -steps[current].Frame - 2;
                }
            }

            if (current >= steps.Count || steps[current].Frame == -1) break;
            SpriteAnimationStep step = steps[current];
            played.Add(new PlayedFrame(current, step.Frame, step.XShift, step.YShift, step.Duration + 1));
        }

        return played;
    }
}
