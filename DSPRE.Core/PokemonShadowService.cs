namespace DSPRE.Core;

public enum ShadowSize { None = 0, Small = 1, Medium = 2, Large = 3 }

/// <summary>How one species is drawn on the battle ground: the size of its shadow, how far the shadow is moved sideways and how far the
/// Pokémon's sprite is moved up or down.</summary>
public sealed class ShadowEntry
{
    public ShadowSize Size { get; set; }
    public sbyte ShadowOffsetX { get; set; }
    public sbyte SpriteOffsetY { get; set; }
}

public sealed class PokemonShadows
{
    public List<ShadowEntry> Species { get; set; } = [];
}

/// <summary>Battle shadows (Platinum): three NARCs of one member each with one byte per species (index 0 is a placeholder):
/// <c>poke_shadow</c> the shadow size (0 none to 3 large), <c>poke_shadow_ofx</c> the shadow's horizontal offset and <c>poke_yofs</c>
/// the sprite's vertical offset, both signed.</summary>
public static class PokemonShadowService
{
    public static string SizePath(RomProject project) => Path.Combine(project.FilesPath, "poketool", "pokegra", "poke_shadow.narc");
    public static string ShadowOffsetPath(RomProject project) => Path.Combine(project.FilesPath, "poketool", "pokegra", "poke_shadow_ofx.narc");
    public static string SpriteOffsetPath(RomProject project) => Path.Combine(project.FilesPath, "poketool", "pokegra", "poke_yofs.narc");

    public static bool IsSupported(RomProject project, RomGameMetadata metadata) =>
        metadata.HeaderFormat == MapHeaderFormat.Platinum && File.Exists(SizePath(project)) && File.Exists(ShadowOffsetPath(project)) && File.Exists(SpriteOffsetPath(project));

    public static PokemonShadows Load(RomProject project)
    {
        byte[] sizes = NarcFile.ReadMember(SizePath(project), 0), shadowX = NarcFile.ReadMember(ShadowOffsetPath(project), 0), spriteY = NarcFile.ReadMember(SpriteOffsetPath(project), 0);
        if (shadowX.Length != sizes.Length || spriteY.Length != sizes.Length) throw new InvalidDataException("The three shadow tables do not have the same number of species.");
        var shadows = new PokemonShadows();
        for (int species = 0; species < sizes.Length; species++)
        {
            if (sizes[species] > (byte)ShadowSize.Large) throw new InvalidDataException($"Species {species} has shadow size {sizes[species]}; the sizes are 0 to 3.");
            shadows.Species.Add(new ShadowEntry { Size = (ShadowSize)sizes[species], ShadowOffsetX = (sbyte)shadowX[species], SpriteOffsetY = (sbyte)spriteY[species] });
        }

        return shadows;
    }

    public static void Save(RomProject project, PokemonShadows shadows) =>
        EditJournal.Track(project, EditJournalDocuments.PokemonShadows, EditJournalDocuments.Keys(), () =>
        {
            int count = NarcFile.ReadMember(SizePath(project), 0).Length;
            if (shadows.Species.Count != count) throw new InvalidDataException($"There are {shadows.Species.Count} species; the tables have {count}.");
            foreach (ShadowEntry entry in shadows.Species)
            {
                if (!Enum.IsDefined(entry.Size)) throw new InvalidDataException($"{entry.Size} is not a shadow size.");
            }

            Write(SizePath(project), shadows.Species.Select(entry => (byte)entry.Size).ToArray());
            Write(ShadowOffsetPath(project), shadows.Species.Select(entry => (byte)entry.ShadowOffsetX).ToArray());
            Write(SpriteOffsetPath(project), shadows.Species.Select(entry => (byte)entry.SpriteOffsetY).ToArray());
        });

    private static void Write(string path, byte[] data)
    {
        if (!data.AsSpan().SequenceEqual(NarcFile.ReadMember(path, 0))) NarcFile.WriteMember(path, 0, 0, data);
    }
}
