using System.Text.Json;
using System.Text.Json.Nodes;

namespace DSPRE.Core;

public sealed class TextArchiveData
{
    internal TextArchiveData(int id, ushort key, string languageCode, JsonObject document, IReadOnlyList<string> messages)
    {
        Id = id;
        Key = key;
        LanguageCode = languageCode;
        Document = document;
        Messages = messages.ToList();
    }

    public int Id { get; }
    public ushort Key { get; }
    public string LanguageCode { get; }
    public List<string> Messages { get; }

    internal JsonObject Document { get; }
}

public static class TextArchiveService
{
    public static IReadOnlyList<int> GetArchiveIds(RomProject project)
    {
        string directory = GetExpandedDirectory(project);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => int.TryParse(name, out _))
            .Select(int.Parse)
            .OrderBy(id => id)
            .ToArray();
    }

    public static TextArchiveData Load(RomProject project, int id, RomGameMetadata metadata)
    {
        string path = GetJsonPath(project, id);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Text archive {id:D4} has not been decoded.", path);
        }

        JsonObject document = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException($"Text archive {id:D4} is not valid JSON.");
        string languageCode = LanguageCode(metadata.Language);
        ushort key = document["key"]?.GetValue<ushort>() ?? 0;
        var messages = new List<string>();
        if (document["messages"] is not JsonArray messageArray)
        {
            throw new InvalidDataException($"Text archive {id:D4} does not contain a messages array.");
        }

        foreach (JsonNode? node in messageArray)
        {
            if (node is not JsonObject message)
            {
                messages.Add(string.Empty);
                continue;
            }

            messages.Add(ReadLocalizedValue(message[languageCode] ?? message["en_US"]));
        }

        return new TextArchiveData(id, key, languageCode, document, messages);
    }

    public static void Save(RomProject project, TextArchiveData archive) =>
        EditJournal.Track(project, EditJournalDocuments.TextArchive, EditJournalDocuments.Keys(("archive", archive.Id)), () => SaveUntracked(project, archive));

    private static void SaveUntracked(RomProject project, TextArchiveData archive)
    {
        if (archive.Document["messages"] is not JsonArray messageArray)
        {
            throw new InvalidDataException($"Text archive {archive.Id:D4} does not contain a messages array.");
        }

        while (messageArray.Count < archive.Messages.Count)
        {
            messageArray.Add(new JsonObject());
        }

        while (messageArray.Count > archive.Messages.Count)
        {
            messageArray.RemoveAt(messageArray.Count - 1);
        }

        for (int i = 0; i < archive.Messages.Count; i++)
        {
            JsonObject message;
            if (messageArray[i] is JsonObject existingMessage)
            {
                message = existingMessage;
            }
            else
            {
                message = new JsonObject();
                messageArray[i] = message;
            }

            // The text encoder rejects messages without an id; new ones get the same positional scheme
            // the decoder assigns (msg_AAAA_IIIII).
            message["id"] ??= MessageId(archive.Id, i);
            message[archive.LanguageCode] = archive.Messages[i];
        }

        string path = GetJsonPath(project, archive.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, archive.Document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Appends a new one-message archive after the highest existing ID and returns its ID.</summary>
    public static int Add(RomProject project)
    {
        IReadOnlyList<int> ids = GetArchiveIds(project);
        int id = ids.Count == 0 ? 0 : ids.Max() + 1;
        string path = GetJsonPath(project, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var document = new JsonObject
        {
            ["key"] = 0,
            ["messages"] = new JsonArray(new JsonObject { ["id"] = MessageId(id, 0), ["en_US"] = "Your text here." })
        };
        File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        EditJournal.Record(project, "CreateTextArchive", ("archive", id));
        return id;
    }

    public static bool RemoveLast(RomProject project)
    {
        IReadOnlyList<int> ids = GetArchiveIds(project);
        if (ids.Count == 0) return false;
        int id = ids[^1];
        File.Delete(GetJsonPath(project, id));
        string binaryPath = Path.Combine(GetUnpackedDirectory(project), $"{id:D4}");
        if (File.Exists(binaryPath)) File.Delete(binaryPath);
        EditJournal.Record(project, "DeleteLastTextArchive", ("archive", id));
        return true;
    }

    /// <summary>Replaces an archive's decoded JSON wholesale with an external JSON file.</summary>
    public static void Import(RomProject project, int id, string sourcePath)
    {
        string fullSource = Path.GetFullPath(sourcePath);
        File.Copy(fullSource, GetJsonPath(project, id), true);
        EditJournal.Record(project, "ImportTextArchive", ("archive", id), ("source", fullSource));
    }

    private static string MessageId(int archiveId, int index) => $"msg_{archiveId:D4}_{index:D5}";

    public static string GetExpandedDirectory(RomProject project) => Path.Combine(project.RootPath, "expanded", "textArchives");

    public static string GetUnpackedDirectory(RomProject project) => Path.Combine(project.RootPath, "unpacked", "textArchives");

    public static string GetJsonPath(RomProject project, int id) => Path.Combine(GetExpandedDirectory(project), $"{id:D4}.json");

    private static string ReadLocalizedValue(JsonNode? value)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out string? text))
        {
            return text ?? string.Empty;
        }

        if (value is JsonArray lines)
        {
            return string.Concat(lines.Select(line => line?.GetValue<string>() ?? string.Empty));
        }

        return string.Empty;
    }

    private static string LanguageCode(RomLanguage language) => language switch
    {
        RomLanguage.English => "en_US",
        RomLanguage.Spanish => "es_ES",
        RomLanguage.Italian => "it_IT",
        RomLanguage.French => "fr_FR",
        RomLanguage.German => "de_DE",
        RomLanguage.Japanese => "ja_JP",
        _ => "en_US"
    };
}