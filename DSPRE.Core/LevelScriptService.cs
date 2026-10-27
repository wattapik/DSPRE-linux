namespace DSPRE.Core;

public enum LevelScriptTriggerType
{
    VariableValue = 1,
    MapChange = 2,
    ScreenReset = 3,
    LoadGame = 4
}

public sealed class LevelScriptTrigger
{
    public LevelScriptTriggerType TriggerType { get; set; }
    public int ScriptTriggered { get; set; }
    public int VariableToWatch { get; set; }
    public int ExpectedValue { get; set; }

    public bool IsVariableValue => TriggerType == LevelScriptTriggerType.VariableValue;

    public static LevelScriptTrigger MapScreenLoad(LevelScriptTriggerType type, int scriptTriggered) =>
        new() { TriggerType = type, ScriptTriggered = scriptTriggered };

    public static LevelScriptTrigger VariableValue(int scriptTriggered, int variableToWatch, int expectedValue) =>
        new()
        {
            TriggerType = LevelScriptTriggerType.VariableValue,
            ScriptTriggered = scriptTriggered,
            VariableToWatch = variableToWatch,
            ExpectedValue = expectedValue
        };

    public string Describe(bool displayInHex)
    {
        string text = $"Starts Script {ScriptTriggered}";
        return TriggerType switch
        {
            LevelScriptTriggerType.MapChange => text + " upon entering the LS map.",
            LevelScriptTriggerType.ScreenReset => text + " when a fadescreen happens in the LS map.",
            LevelScriptTriggerType.LoadGame => text + " when the game resumes in the LS map.",
            LevelScriptTriggerType.VariableValue =>
                text + " when Var " + Format(VariableToWatch, displayInHex) + " == " + Format(ExpectedValue, displayInHex),
            _ => text + " under unknown circumstances."
        };
    }

    private static string Format(int value, bool displayInHex) => displayInHex ? "0x" + value.ToString("X") : value.ToString();
}

public sealed class LevelScriptFile
{
    public LevelScriptFile(int id) => Id = id;

    public int Id { get; set; }
    public List<LevelScriptTrigger> Triggers { get; } = [];
}

public static class LevelScriptService
{
    private const int SmallestTriggerSize = 5;

    public static string GetPath(RomProject project, int id) => Path.Combine(project.ScriptsDirectory, $"{id:D4}");

    public static IReadOnlyList<int> GetIds(RomProject project)
    {
        if (!Directory.Exists(project.ScriptsDirectory))
        {
            return [];
        }

        return Directory.GetFiles(project.ScriptsDirectory)
            .Select(path => (Path: path, Name: Path.GetFileNameWithoutExtension(path)))
            .Where(entry => int.TryParse(entry.Name, out _))
            .Where(entry => IsLevelScript(entry.Path))
            .Select(entry => int.Parse(entry.Name))
            .OrderBy(id => id)
            .ToArray();
    }

    /// <summary>Some regular scripts carry no 0xFD13 marker, so the file must parse before it counts as a level script.</summary>
    public static bool IsLevelScript(string path)
    {
        try
        {
            LoadPath(path, 0);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static LevelScriptFile Load(RomProject project, int id) => LoadPath(GetPath(project, id), id);

    public static LevelScriptFile LoadPath(string path, int id)
    {
        var file = new LevelScriptFile(id);
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new BinaryReader(stream);

        if (!DetectLevelScript(reader))
        {
            throw new InvalidDataException("The file you attempted to load is not a Level Script file.");
        }

        stream.Position = 0;
        bool hasConditionalStructure = false;
        int conditionalStructureOffset = -1;

        while (stream.Position < stream.Length)
        {
            byte triggerType = reader.ReadByte();
            if (!IsValidTriggerType(triggerType))
            {
                break;
            }

            if (hasConditionalStructure)
            {
                conditionalStructureOffset -= sizeof(byte);
            }

            if (triggerType == (byte)LevelScriptTriggerType.VariableValue)
            {
                if (stream.Position + sizeof(uint) > stream.Length)
                {
                    break;
                }

                hasConditionalStructure = true;
                conditionalStructureOffset = (int)reader.ReadUInt32();
                continue;
            }

            if (stream.Position + sizeof(uint) > stream.Length)
            {
                break;
            }

            uint scriptToTrigger = reader.ReadUInt32();
            file.Triggers.Add(LevelScriptTrigger.MapScreenLoad((LevelScriptTriggerType)triggerType, (int)scriptToTrigger));

            if (hasConditionalStructure)
            {
                conditionalStructureOffset -= sizeof(uint);
            }
        }

        if (stream.Position == 1 && stream.Length < SmallestTriggerSize)
        {
            return file;
        }

        if (stream.Position < SmallestTriggerSize)
        {
            throw new InvalidDataException("Parser failure: The input file you attempted to load is either malformed or not a Level Script file.");
        }

        if (!hasConditionalStructure)
        {
            return file;
        }

        if (conditionalStructureOffset != 1)
        {
            throw new InvalidDataException($"Field error: The Level Script file you attempted to load is broken. {conditionalStructureOffset}");
        }

        while (stream.Position + 6 <= stream.Length)
        {
            int variableId = reader.ReadUInt16();
            if (variableId <= 0)
            {
                break;
            }

            int expectedValue = reader.ReadUInt16();
            int scriptToTrigger = reader.ReadUInt16();
            file.Triggers.Add(LevelScriptTrigger.VariableValue(scriptToTrigger, variableId, expectedValue));
        }

        return file;
    }

    public static byte[] ToByteArray(LevelScriptFile file, bool wordAlignmentPadding = false)
    {
        using MemoryStream stream = new MemoryStream();
        using (BinaryWriter writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            var mapScreenLoadTriggers = file.Triggers
                .Where(trigger => !trigger.IsVariableValue)
                .Select(trigger => (trigger.TriggerType, trigger.ScriptTriggered))
                .Distinct()
                .ToList();
            var variableValueTriggers = file.Triggers
                .Where(trigger => trigger.IsVariableValue)
                .Select(trigger => (trigger.VariableToWatch, trigger.ExpectedValue, trigger.ScriptTriggered))
                .Distinct()
                .ToList();

            foreach ((LevelScriptTriggerType type, int scriptTriggered) in mapScreenLoadTriggers)
            {
                writer.Write((byte)type);
                writer.Write((uint)scriptTriggered);
            }

            if (variableValueTriggers.Count > 0)
            {
                writer.Write((byte)LevelScriptTriggerType.VariableValue);
                writer.Write((uint)1);
                writer.Write((byte)0);
                foreach ((int variable, int expected, int script) in variableValueTriggers)
                {
                    writer.Write((ushort)variable);
                    writer.Write((ushort)expected);
                    writer.Write((ushort)script);
                }
            }

            writer.Write((ushort)0);

            if (wordAlignmentPadding)
            {
                long missingBytes = writer.BaseStream.Position % 4;
                for (int i = 0; i < 4 - missingBytes; i++)
                {
                    writer.Write((byte)0);
                }
            }
        }

        return stream.ToArray();
    }

    public static void Save(RomProject project, LevelScriptFile file, bool wordAlignmentPadding = false) =>
        EditJournal.Track(project, EditJournalDocuments.LevelScript, EditJournalDocuments.Keys(("levelScript", file.Id), ("padding", wordAlignmentPadding)), () => SaveUntracked(project, file, wordAlignmentPadding));

    private static void SaveUntracked(RomProject project, LevelScriptFile file, bool wordAlignmentPadding = false)
    {
        Directory.CreateDirectory(project.ScriptsDirectory);
        File.WriteAllBytes(GetPath(project, file.Id), ToByteArray(file, wordAlignmentPadding));
    }

    private static bool IsValidTriggerType(byte triggerType) =>
        triggerType is >= (byte)LevelScriptTriggerType.VariableValue and <= (byte)LevelScriptTriggerType.LoadGame;

    /// <summary>A file is a level script until the 0xFD13 script magic number proves otherwise.</summary>
    private static bool DetectLevelScript(BinaryReader reader)
    {
        Stream stream = reader.BaseStream;
        stream.Position = 0;
        int offsetCount = 0;

        while (stream.Position + sizeof(uint) <= stream.Length)
        {
            uint checker = reader.ReadUInt16();
            stream.Position -= 0x2;
            uint value = reader.ReadUInt32();

            if (value == 0 && offsetCount == 0)
            {
                return true;
            }

            if (checker == 0xFD13)
            {
                return false;
            }

            offsetCount++;
        }

        return true;
    }
}
