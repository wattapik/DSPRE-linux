using System.Text;

namespace DSPRE.Core;

/// <summary>Adding and removing map headers, their files on disk, and the blank files a new header can
/// start with. Ported from legacy DS_Map/Editors/HeaderEditor.cs (addHeaderBTN_Click,
/// removeLastHeaderBTN_Click, import/exportHeaderToFileButton_Click, CreateSupportingFiles).</summary>
public static class HeaderFileService
{
    private const int InternalNameLength = 16;
    private const string InternalNameSignature = "INTNAME";
    public const string NewHeaderName = "NEWMAP";

    /// <summary>A header file as legacy writes it: the 24 header bytes, a padding byte, "INTNAME", then
    /// the internal name - at most 48 bytes.</summary>
    public static byte[] Export(MapHeaderRecord header, string internalName)
    {
        using var stream = new MemoryStream();
        stream.Write(header.Serialize());
        stream.WriteByte(0);
        stream.Write(Encoding.UTF8.GetBytes(InternalNameSignature));
        stream.Write(Encoding.UTF8.GetBytes(internalName));
        return stream.ToArray();
    }

    /// <summary>Replaces header <paramref name="id"/> with a header file's contents, and its internal name
    /// too when the file carries one. Both saves are journaled as ordinary header edits.</summary>
    public static MapHeaderRecord Import(RomProject project, IMapHeaderStore store, RomGameMetadata metadata, ushort id, string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < MapHeaderRecord.SerializedLength || data.Length > MapHeaderRecord.SerializedLength + 8 + InternalNameLength)
        {
            throw new InvalidDataException("This is not a header file: a header file is 24 to 48 bytes long.");
        }

        MapHeaderRecord header = MapHeaderRecord.Parse(data.AsSpan(0, MapHeaderRecord.SerializedLength), id, metadata.HeaderFormat);
        store.Save(header);

        int nameOffset = MapHeaderRecord.SerializedLength + 8;
        if (data.Length > nameOffset)
        {
            string name = Encoding.UTF8.GetString(data, nameOffset, Math.Min(InternalNameLength, data.Length - nameOffset)).TrimEnd('\0', ' ');
            if (name.Length > 0) project.SaveInternalName(id, name);
        }

        return header;
    }

    /// <summary>Headers can only be added or removed once the ROM loads them from the dynamic-headers NARC
    /// (the Dynamic Header Allocation patch); the ARM9's own header table has a fixed size.</summary>
    public static bool CanChangeHeaderCount(RomProject project, RomGameMetadata metadata) =>
        DynamicHeadersPatch.IsApplied(project, metadata) && Directory.Exists(project.DynamicHeadersDirectory);

    /// <summary>Appends a copy of header 0000 named "NEWMAP"; returns its id.</summary>
    public static int Add(RomProject project, RomGameMetadata metadata)
    {
        RequireDynamicHeaders(project, metadata);
        int id = project.GetHeaderCount();
        File.Copy(Path.Combine(project.DynamicHeadersDirectory, "0000"), Path.Combine(project.DynamicHeadersDirectory, id.ToString("D4")), overwrite: true);
        using (var names = new FileStream(project.InternalNamesPath, FileMode.Append, FileAccess.Write))
        {
            byte[] name = new byte[InternalNameLength];
            Encoding.UTF8.GetBytes(NewHeaderName).CopyTo(name, 0);
            names.Write(name);
        }

        EditJournal.Record(project, "CreateHeader", ("header", id));
        return id;
    }

    /// <summary>Deletes the last header and its internal name. At least one header always stays.</summary>
    public static bool RemoveLast(RomProject project, RomGameMetadata metadata)
    {
        RequireDynamicHeaders(project, metadata);
        int count = project.GetHeaderCount();
        if (count <= 1) return false;

        int id = count - 1;
        string path = Path.Combine(project.DynamicHeadersDirectory, id.ToString("D4"));
        if (File.Exists(path)) File.Delete(path);
        using (var names = new FileStream(project.InternalNamesPath, FileMode.Open, FileAccess.Write))
        {
            names.SetLength((long)id * InternalNameLength);
        }

        EditJournal.Record(project, "DeleteLastHeader", ("header", id));
        return true;
    }

    private static void RequireDynamicHeaders(RomProject project, RomGameMetadata metadata)
    {
        if (!CanChangeHeaderCount(project, metadata))
        {
            throw new InvalidOperationException("Headers can only be added or removed after the Dynamic Header Allocation patch (ROM Toolbox).");
        }
    }

    public sealed record SupportingFiles(int TextArchive, int ScriptFile, int LevelScript, int EventFile);

    /// <summary>The blank files legacy offers to create with a new header: a text archive, a script file
    /// (one empty script), a level script (running script 1 on entering the map) and an event file (no
    /// events). The script file is written by the legacy bridge, so its binary and plaintext come out
    /// exactly as legacy's "Add script file" makes them.</summary>
    public static async Task<SupportingFiles> CreateSupportingFilesAsync(RomProject project, RomGameMetadata metadata, string toolsDirectory)
    {
        int textArchive = TextArchiveService.Add(project);
        int scriptFile = await AddScriptFileAsync(project, metadata, toolsDirectory);
        int levelScript = AddLevelScript(project);
        int eventFile = AddEventFile(project);
        return new SupportingFiles(textArchive, scriptFile, levelScript, eventFile);
    }

    /// <summary>One past the highest numbered file, ignoring anything else in the folder (backups).</summary>
    public static int NextFileId(string directory) => !Directory.Exists(directory) ? 0 : Directory.GetFiles(directory)
        .Select(path => int.TryParse(Path.GetFileName(path), out int id) ? id : -1)
        .DefaultIfEmpty(-1)
        .Max() + 1;

    public static async Task<int> AddScriptFileAsync(RomProject project, RomGameMetadata metadata, string toolsDirectory)
    {
        int id = NextFileId(project.ScriptsDirectory);
        await CreateScriptFileAsync(project, metadata, toolsDirectory, id);
        EditJournal.Record(project, "CreateScriptFile", ("script", id));
        return id;
    }

    public static async Task CreateScriptFileAsync(RomProject project, RomGameMetadata metadata, string toolsDirectory, int id)
    {
        ToolResult result = await new RomExtractionService().RunToolboxAsync(project, metadata, toolsDirectory, ["create-script", id.ToString()]);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Script file {id} could not be created: {result.StandardError.Trim()}");
        }
    }

    public static int AddLevelScript(RomProject project)
    {
        int id = NextFileId(project.ScriptsDirectory);
        WriteBlankLevelScript(project, id);
        EditJournal.Record(project, "CreateLevelScript", ("levelScript", id));
        return id;
    }

    public static void WriteBlankLevelScript(RomProject project, int id)
    {
        var file = new LevelScriptFile(id);
        file.Triggers.Add(LevelScriptTrigger.MapScreenLoad(LevelScriptTriggerType.MapChange, 1));
        Directory.CreateDirectory(project.ScriptsDirectory);
        File.WriteAllBytes(LevelScriptService.GetPath(project, id), LevelScriptService.ToByteArray(file));
    }

    public static int AddEventFile(RomProject project)
    {
        int id = NextFileId(project.EventsDirectory);
        WriteBlankEventFile(project, id);
        EditJournal.Record(project, "CreateEventFile", ("eventFile", id));
        return id;
    }

    /// <summary>Four zero counts: no spawnables, overworlds, warps or triggers.</summary>
    public static void WriteBlankEventFile(RomProject project, int id)
    {
        Directory.CreateDirectory(project.EventsDirectory);
        File.WriteAllBytes(MapEventService.GetPath(project, id), new byte[16]);
    }
}
