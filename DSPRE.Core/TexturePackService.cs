namespace DSPRE.Core;

public enum TexturePackKind
{
    Map,
    Building
}

/// <summary>Adds, removes and replaces whole NSBTX texture packs. Building packs have a same-numbered
/// config file alongside them that is kept in step.</summary>
public static class TexturePackService
{
    public static string GetDirectory(RomProject project, TexturePackKind kind) =>
        kind == TexturePackKind.Map ? project.MapTexturesDirectory : project.BuildingTexturesDirectory;

    public static string GetPath(RomProject project, TexturePackKind kind, int id) =>
        Path.Combine(GetDirectory(project, kind), $"{id:D4}");

    public static int Count(RomProject project, TexturePackKind kind) =>
        Directory.Exists(GetDirectory(project, kind)) ? Directory.GetFiles(GetDirectory(project, kind)).Length : 0;

    /// <summary>Appends a copy of pack 0000 (and its building config), matching the legacy editor.</summary>
    public static int Add(RomProject project, TexturePackKind kind)
    {
        string source = GetPath(project, kind, 0);
        if (!File.Exists(source)) throw new FileNotFoundException("Texture Pack 0000 is required to add a new NSBTX slot.", source);

        int id = Count(project, kind);
        File.Copy(source, GetPath(project, kind, id), false);
        if (kind == TexturePackKind.Building)
        {
            string sourceConfig = Path.Combine(project.BuildingConfigsDirectory, "0000");
            if (File.Exists(sourceConfig)) File.Copy(sourceConfig, Path.Combine(project.BuildingConfigsDirectory, $"{id:D4}"), false);
        }

        EditJournal.Record(project, "CreateTexturePack", ("kind", kind.ToString()), ("pack", id));
        return id;
    }

    public static bool RemoveLast(RomProject project, TexturePackKind kind)
    {
        int count = Count(project, kind);
        if (count <= 1) return false;

        int id = count - 1;
        File.Delete(GetPath(project, kind, id));
        if (kind == TexturePackKind.Building)
        {
            string configPath = Path.Combine(project.BuildingConfigsDirectory, $"{id:D4}");
            if (File.Exists(configPath)) File.Delete(configPath);
        }

        EditJournal.Record(project, "DeleteLastTexturePack", ("kind", kind.ToString()), ("pack", id));
        return true;
    }

    /// <summary>Rewrites one palette's colours (BGR555) in place. Colours past the palette's length are
    /// ignored; a shorter list leaves the rest of the palette as it was.</summary>
    public static void SetPalette(RomProject project, TexturePackKind kind, int id, string paletteName, IReadOnlyList<ushort> colors)
    {
        string path = GetPath(project, kind, id);
        byte[] data = File.ReadAllBytes(path);
        NsbtxPaletteLayout.PaletteSlot slot = NsbtxPaletteLayout.Read(data).FirstOrDefault(palette => palette.Name == paletteName)
            ?? throw new InvalidDataException($"Texture pack {id:D4} has no palette named '{paletteName}'.");

        int written = Math.Min(colors.Count, slot.Length / 2);
        for (int i = 0; i < written; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(slot.Offset + 2 * i), colors[i]);
        }

        File.WriteAllBytes(path, data);
        EditJournal.Record(project, "SetTexturePackPalette", ("kind", kind.ToString()), ("pack", id), ("palette", paletteName),
            ("colors", new System.Text.Json.Nodes.JsonArray(colors.Take(written).Select(color => (System.Text.Json.Nodes.JsonNode?)(int)color).ToArray())));
    }

    public static void Import(RomProject project, TexturePackKind kind, int id, string sourcePath)
    {
        string fullSource = Path.GetFullPath(sourcePath);
        File.Copy(fullSource, GetPath(project, kind, id), true);
        EditJournal.Record(project, "ImportTexturePack", ("kind", kind.ToString()), ("pack", id), ("source", fullSource));
    }
}
