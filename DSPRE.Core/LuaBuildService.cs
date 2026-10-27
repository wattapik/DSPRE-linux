using System.Text.RegularExpressions;
using DSPRE.Core.Lua;

namespace DSPRE.Core;

/// <summary>Compiles every Lua script file of a project into its .script (and rebuilds the text archive of its messages), as saving
/// it in the Script Editor does, so scripts edited in another program build the same way. Errors are collected per file instead of
/// stopping at the first.</summary>
public static partial class LuaBuildService
{
    public sealed record Diagnostic(string File, int Line, string Message)
    {
        public override string ToString() => Line > 0 ? $"{File}:{Line}: {Message}" : $"{File}: {Message}";
    }

    public sealed record Result(int Files, IReadOnlyList<Diagnostic> Errors, IReadOnlyList<Diagnostic> Warnings);

    [GeneratedRegex(@"^(?:global\.lua, line \d+|Line \d+): ")]
    private static partial Regex LinePrefix();

    /// <param name="write">Save the compiled .script files and text archives; off, only check.</param>
    public static Result Compile(RomProject project, RomGameMetadata metadata, string databasesDirectory, bool write)
    {
        var errors = new List<Diagnostic>();
        var warnings = new List<Diagnostic>();
        if (!Directory.Exists(project.ExpandedScriptsDirectory)) return new Result(0, errors, warnings);
        string[] files = Directory.GetFiles(project.ExpandedScriptsDirectory, "*.lua").Where(path => int.TryParse(Path.GetFileNameWithoutExtension(path), out _))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (files.Length == 0) return new Result(0, errors, warnings);

        ScriptLanguage language;
        try
        {
            language = LuaScriptService.LoadLanguage(project, metadata, databasesDirectory);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            errors.Add(new Diagnostic("script command database", 0, ex.Message));
            return new Result(files.Length, errors, warnings);
        }

        string? global = LuaScriptService.LoadGlobal(project);
        var archiveLookup = new ScriptArchiveLookup(project, metadata);

        void AddError(string file, int line, string message)
        {
            var diagnostic = new Diagnostic(file, line, LinePrefix().Replace(message, ""));
            if (!errors.Contains(diagnostic)) errors.Add(diagnostic);
        }

        foreach (string path in files)
        {
            int id = int.Parse(Path.GetFileNameWithoutExtension(path));
            string name = Path.GetFileName(path);
            try
            {
                TextArchiveData? archive = archiveLookup.Find(id);
                ScriptMessageUse use = archive is null ? ScriptMessageUse.Unknown : ScriptMessageAllocator.FindUse(project, metadata, language, id, archive.Id);
                LuaScriptService.SaveResult result = LuaScriptService.CompileForSave(language, File.ReadAllText(path), global, archive?.Messages, use);
                warnings.AddRange(result.Warnings.Select(warning => new Diagnostic(name, 0, warning)));
                if (!write) continue;
                if (archive is not null && result.Messages is not null && !archive.Messages.SequenceEqual(result.Messages))
                {
                    archive.Messages.Clear();
                    archive.Messages.AddRange(result.Messages);
                    TextArchiveService.Save(project, archive);
                }

                ScriptFileService.Save(project, id, result.ScriptText);
            }
            catch (LuaException ex)
            {
                if (ex.InGlobal) AddError("global.lua", ex.Line - LuaException.GlobalLineBase, ex.Message);
                else AddError(name, ex.Line, ex.Message);
            }
            catch (ScriptTextException ex)
            {
                AddError(name, ex.Line, ex.Message);
            }
            catch (InvalidDataException ex)
            {
                AddError(name, 0, ex.Message);
            }
        }

        return new Result(files.Length, errors, warnings);
    }
}
