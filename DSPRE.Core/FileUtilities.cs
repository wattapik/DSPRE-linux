using System.Text;
using System.Text.RegularExpressions;

namespace DSPRE.Core;

/// <summary>The legacy editor's general file utilities (DS_Map "Main Window.cs" and Helpers.cs): list
/// files that enumerate a folder's contents, and batch renames driven by such a list or by what each
/// file contains. They work on any folder the user picks, not on the open project.</summary>
public static class FileUtilities
{
    private const string CommentPrefix = "#";
    public const string IsolatedFolderName = "DSPRE_IsolatedFiles";

    /// <summary>A folder's files in natural order: runs of digits compare as numbers, so "2" sorts
    /// before "10". Legacy OpenNonEmptyDir padded numbers only to the width of the file count, which
    /// put "10" before "2" in a folder of fewer than ten files; this pads to the longest number.</summary>
    public static List<FileInfo> NaturalOrderFiles(string directory)
    {
        FileInfo[] files = new DirectoryInfo(directory).GetFiles();
        int width = files.SelectMany(file => Regex.Matches(file.Name, @"\d+").Select(match => match.Length)).DefaultIfEmpty(0).Max();
        return files
            .OrderBy(file => Regex.Replace(file.Name, @"\d+", match => match.Value.PadLeft(width, '0')), StringComparer.Ordinal)
            .ToList();
    }

    public static List<string> ListHeader(string description) =>
    [
        "#============================================================================",
        $"# {description}",
        "#============================================================================"
    ];

    /// <summary>A list file naming every file of the folder, one per line, in natural order.</summary>
    public static void BuildListFromFolder(string directory, string listPath)
    {
        List<string> lines = ListHeader($"File enumeration definition for folder \"{Path.GetFileName(Path.TrimEndingDirectorySeparator(directory))}\"");
        lines.AddRange(NaturalOrderFiles(directory).Select(file => file.Name));
        File.WriteAllLines(listPath, lines);
    }

    /// <summary>Legacy's Enum-Based List Builder: a list file from a C enum, one entry per line, ordered
    /// by value (explicit "= N" values reset the count). The prefix the first two entries share up to an
    /// underscore is dropped and each entry's last underscore becomes a dot, so FIELD_MAP_town01_nsbmd and
    /// FIELD_MAP_town02_nsbmd give town01.nsbmd and town02.nsbmd. Returns how many entries were written.</summary>
    public static int BuildListFromEnum(string enumPath, string listPath)
    {
        string[] lines = File.ReadAllLines(enumPath).Select(line => line.Trim()).ToArray();
        int start = Array.FindIndex(lines, line => line.Replace(" ", "").Contains("enum{", StringComparison.Ordinal));
        if (start < 0) throw new InvalidDataException("No \"enum {\" line was found.");
        int end = Array.FindIndex(lines, start + 1, line => line.Replace(" ", "").Contains("};", StringComparison.Ordinal));
        if (end < 0) throw new InvalidDataException("The enum has no closing \"};\".");

        // Blank and comment-only lines inside the enum are not entries.
        string[] entryLines = lines[(start + 1)..end]
            .Select(line => line.IndexOf("//", StringComparison.Ordinal) is int comment and >= 0 ? line[..comment].Trim() : line)
            .Where(line => line.Length > 0)
            .ToArray();
        if (entryLines.Length < 2) throw new InvalidDataException("The enum needs at least 2 entries.");

        int common = entryLines[0].Zip(entryLines[1], (a, b) => a == b).TakeWhile(same => same).Count();
        int prefixEnd = entryLines[0][..common].LastIndexOf('_');
        var entries = new SortedDictionary<int, string>();
        int next = 0;
        foreach (string line in entryLines)
        {
            string rest = line[(prefixEnd + 1)..].Trim().Replace(",", "");
            int equals = rest.LastIndexOf('=');
            string name = (equals >= 0 ? rest[..equals] : rest).Trim();
            if (equals >= 0)
            {
                string value = rest[(equals + 1)..].Trim();
                next = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToInt32(value[2..], 16)
                    : int.TryParse(value, out int number) ? number : throw new InvalidDataException($"\"{line}\" has a value that is not a number.");
            }

            int underscore = name.LastIndexOf('_');
            if (underscore >= 0) name = name[..underscore] + "." + name[(underscore + 1)..];
            if (!entries.TryAdd(next, name)) throw new InvalidDataException($"Two entries have the value {next}.");
            next++;
        }

        List<string> output = ListHeader($"File enumeration definition based on \"{Path.GetFileNameWithoutExtension(enumPath)}\"");
        output.AddRange(entries.Values);
        File.WriteAllLines(listPath, output);
        return entries.Count;
    }

    /// <summary>The usable lines of a list file: blank lines and "#" comments are skipped.</summary>
    public static List<string> ReadList(string listPath) =>
        File.ReadAllLines(listPath).Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith(CommentPrefix, StringComparison.Ordinal)).ToList();

    public sealed record RenameResult(int Renamed, int Unnamed, int Isolated);

    /// <summary>Renames the folder's files, in natural order, to the list's names. A name already taken
    /// gets "(1)", "(2)"... appended. Files left over when the list is shorter stay as they are, or are
    /// moved into <see cref="IsolatedFolderName"/> when <paramref name="isolateUnnamed"/> is set.</summary>
    public static RenameResult RenameFromList(string directory, IReadOnlyList<string> names, bool isolateUnnamed)
    {
        List<FileInfo> files = NaturalOrderFiles(directory);
        int count = Math.Min(files.Count, names.Count);
        for (int i = 0; i < count; i++)
        {
            string name = names[i].Trim();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
            string destination = UniquePath(Path.Combine(directory, name), files[i].FullName);
            if (!string.Equals(destination, files[i].FullName, StringComparison.Ordinal)) File.Move(files[i].FullName, destination);
        }

        int isolated = 0;
        if (isolateUnnamed && files.Count > count)
        {
            string isolatedDirectory = Path.Combine(directory, IsolatedFolderName);
            Directory.CreateDirectory(isolatedDirectory);
            foreach (FileInfo file in files.Skip(count))
            {
                File.Move(file.FullName, UniquePath(Path.Combine(isolatedDirectory, file.Name), null));
                isolated++;
            }
        }

        return new RenameResult(count, files.Count - count, isolated);
    }

    /// <summary>Renames each file after what it holds, as legacy ContentBasedBatchRename does: Nitro
    /// files (BMD0, BTX0, BCA0...) get the name stored inside them (models and animations keep their own
    /// name when it is unreadable; textures always keep theirs) and an ".ns" + magic extension, e.g.
    /// "t1_h01.nsbmd"; other files keep their name and get their 4-letter magic as extension (an
    /// RLCN/RGCN palette or graphic, read at offset 5 past an "R" signature check, likewise). Returns the
    /// new names, in order - a list file of them describes the folder.</summary>
    public static List<string> RenameByContent(string directory)
    {
        var renamed = new List<string>();
        foreach (FileInfo file in NaturalOrderFiles(directory))
        {
            byte[] head = new byte[16];
            using (FileStream stream = file.OpenRead())
            {
                if (stream.Read(head, 0, head.Length) < head.Length) continue;
            }

            string baseName = Path.GetFileNameWithoutExtension(file.Name);
            string magic;
            string name;
            if (head[0] == 'B' && head[3] == '0')
            {
                magic = new string([.. head.Take(3).Select(b => char.ToLowerInvariant((char)b))]);
                name = baseName;
                if (!(head[1] == 'T' && head[2] == 'X'))
                {
                    int nameOffset = 52 + 4 * (BitConverter.ToUInt16(head, 0xE) - 1);
                    string? stored = ReadName(file.FullName, nameOffset);
                    if (!string.IsNullOrEmpty(stored) && stored.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) name = stored;
                }

                name += ".ns" + magic;
            }
            else
            {
                int offset = head[5] == 'R' && head[8] == 'N' ? 5 : 0;
                magic = new string([.. head.Skip(offset).Take(4).Select(b => char.ToLowerInvariant((char)b))]);
                name = baseName + "." + magic;
            }

            if (magic.Length == 0 || !magic.All(char.IsLetterOrDigit)) continue;
            string destination = UniquePath(Path.Combine(directory, name), file.FullName);
            if (!string.Equals(destination, file.FullName, StringComparison.Ordinal)) File.Move(file.FullName, destination);
            renamed.Add(Path.GetFileName(destination));
        }

        return renamed;
    }

    private static string? ReadName(string path, int offset)
    {
        using FileStream stream = File.OpenRead(path);
        if (offset < 0 || offset + 16 > stream.Length) return null;
        byte[] buffer = new byte[16];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return Encoding.UTF8.GetString(buffer).TrimEnd('\0');
    }

    /// <summary><paramref name="path"/>, or "name(1).ext", "name(2).ext"... when it is taken by a file
    /// other than <paramref name="self"/>.</summary>
    private static string UniquePath(string path, string? self)
    {
        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        string candidate = path;
        for (int append = 1; File.Exists(candidate) && !string.Equals(candidate, self, StringComparison.Ordinal); append++)
        {
            candidate = Path.Combine(directory, $"{name}({append}){extension}");
        }

        return candidate;
    }
}
