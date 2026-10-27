namespace DSPRE.Core;

/// <summary>The names a header's location can take: the messages of one text archive (DP 382, Platinum 433, HeartGold/SoulSilver 279,
/// 272 in Japanese), where a location's number is the message's position. A new name is a message added at the end.</summary>
public static class LocationNameService
{
    public static int ArchiveId(RomGameMetadata metadata) => metadata.HeaderFormat switch
    {
        MapHeaderFormat.DiamondPearl => 382,
        MapHeaderFormat.Platinum => 433,
        MapHeaderFormat.HeartGoldSoulSilver => metadata.Language == RomLanguage.Japanese ? 272 : 279,
        _ => throw new ArgumentOutOfRangeException(nameof(metadata))
    };

    /// <summary>Adds the name as a new location and returns its number.</summary>
    public static int Add(RomProject project, RomGameMetadata metadata, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A location name cannot be empty.", nameof(name));
        TextArchiveData archive = TextArchiveService.Load(project, ArchiveId(metadata), metadata);
        archive.Messages.Add(name);
        TextArchiveService.Save(project, archive);
        return archive.Messages.Count - 1;
    }
}
