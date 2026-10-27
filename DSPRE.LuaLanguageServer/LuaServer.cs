using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DSPRE.Core;
using DSPRE.Core.Lua;

namespace DSPRE.LuaLanguageServer;

/// <summary>The language server: tracks the open Lua files, checks them with DSPRE's own Lua compiler and answers the editor's
/// requests from <see cref="LuaFeatures"/>.</summary>
internal sealed partial class LuaServer(JsonRpc rpc, string databasesDirectory)
{
    private sealed record Document(string Text, int Version);

    private readonly ConcurrentDictionary<string, Document> documents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProjectContext?> projects = new(StringComparer.Ordinal);
    private bool shutdownRequested;
    private bool warnedAboutDatabase;

    [GeneratedRegex(@"^(?:global\.lua, line \d+|Line \d+): ")]
    private static partial Regex LinePrefix();

    public int Run()
    {
        while (rpc.Read() is JsonNode message)
        {
            string? method = message["method"]?.GetValue<string>();
            JsonNode? id = message["id"];
            if (method is null) continue;
            try
            {
                JsonNode? result = Handle(method, message["params"]);
                if (id is not null) rpc.Respond(id, result);
                if (method == "exit") return shutdownRequested ? 0 : 1;
            }
            catch (RequestFailed failure)
            {
                if (id is not null) rpc.Fail(id, -32803, failure.Message);
            }
            catch (MethodNotFound) when (id is not null)
            {
                rpc.Fail(id, -32601, $"Method not found: {method}");
            }
            catch (MethodNotFound)
            {
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{method}: {ex}");
                if (id is not null) rpc.Respond(id, null);
            }
        }
        return shutdownRequested ? 0 : 1;
    }

    private sealed class MethodNotFound : Exception;

    private sealed class RequestFailed(string message) : Exception(message);

    private JsonNode? Handle(string method, JsonNode? parameters)
    {
        switch (method)
        {
            case "initialize": return Initialize();
            case "initialized" or "$/cancelRequest" or "$/setTrace" or "workspace/didChangeConfiguration" or "textDocument/willSave": return null;
            case "shutdown":
                shutdownRequested = true;
                return null;
            case "exit": return null;
            case "textDocument/didOpen":
            {
                JsonNode doc = parameters!["textDocument"]!;
                string uri = doc["uri"]!.GetValue<string>();
                documents[uri] = new Document(doc["text"]!.GetValue<string>(), doc["version"]?.GetValue<int>() ?? 0);
                Changed(uri);
                return null;
            }
            case "textDocument/didChange":
            {
                string uri = parameters!["textDocument"]!["uri"]!.GetValue<string>();
                JsonArray changes = parameters["contentChanges"]!.AsArray();
                documents[uri] = new Document(changes[^1]!["text"]!.GetValue<string>(), parameters["textDocument"]!["version"]?.GetValue<int>() ?? 0);
                Changed(uri);
                return null;
            }
            case "textDocument/didSave":
                Changed(parameters!["textDocument"]!["uri"]!.GetValue<string>());
                return null;
            case "textDocument/didClose":
            {
                string uri = parameters!["textDocument"]!["uri"]!.GetValue<string>();
                documents.TryRemove(uri, out _);
                if (pending.TryRemove(uri, out CancellationTokenSource? source)) source.Cancel();
                rpc.Notify("textDocument/publishDiagnostics", new JsonObject { ["uri"] = uri, ["diagnostics"] = new JsonArray() });
                if (IsGlobal(uri)) RecheckAll();
                return null;
            }
            case "workspace/didChangeWatchedFiles":
                RecheckAll();
                return null;
            case "textDocument/completion": return WithDocument(parameters, (text, tokens, line, column, project) => LuaFeatures.Complete(text, tokens, line, column, project));
            case "textDocument/hover": return WithDocument(parameters, (_, tokens, line, column, project) => LuaFeatures.Hover(tokens, line, column, project));
            case "textDocument/signatureHelp": return WithDocument(parameters, (_, tokens, line, column, project) => LuaFeatures.SignatureHelp(tokens, line, column, project));
            case "textDocument/definition": return Definition(parameters!);
            case "textDocument/documentSymbol":
            {
                string uri = parameters!["textDocument"]!["uri"]!.GetValue<string>();
                if (!documents.TryGetValue(uri, out Document? doc)) return new JsonArray();
                return LuaFeatures.Symbols(LuaTokens.Tokenize(doc.Text), ProjectOf(uri), IsGlobal(uri));
            }
            case "textDocument/formatting": return Format(parameters!);
            case "textDocument/references": return References(parameters!);
            case "textDocument/prepareRename": return PrepareRename(parameters!);
            case "textDocument/rename": return Rename(parameters!);
            case "textDocument/semanticTokens/full":
            {
                string uri = parameters!["textDocument"]!["uri"]!.GetValue<string>();
                return documents.TryGetValue(uri, out Document? doc) ? LuaRefactor.SemanticTokens(LuaTokens.Tokenize(doc.Text), ProjectOf(uri)) : null;
            }
            default: throw new MethodNotFound();
        }
    }

    private static JsonObject Initialize() => new()
    {
        ["capabilities"] = new JsonObject
        {
            ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = 1, ["save"] = new JsonObject { ["includeText"] = false } },
            ["completionProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray("[", ".", "("), ["resolveProvider"] = false },
            ["hoverProvider"] = true,
            ["definitionProvider"] = true,
            ["signatureHelpProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray("(", ",") },
            ["documentSymbolProvider"] = true,
            ["documentFormattingProvider"] = true,
            ["referencesProvider"] = true,
            ["renameProvider"] = new JsonObject { ["prepareProvider"] = true },
            ["semanticTokensProvider"] = new JsonObject { ["legend"] = LuaRefactor.SemanticLegend(), ["full"] = true },
        },
        ["serverInfo"] = new JsonObject { ["name"] = "dspre-lua-ls" },
    };

    private static string PathOf(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) && parsed.IsFile ? parsed.LocalPath : uri;

    private static string UriOf(string path) => new Uri(path).AbsoluteUri;

    private bool IsGlobal(string uri) => string.Equals(Path.GetFileName(PathOf(uri)), "global.lua", StringComparison.OrdinalIgnoreCase);

    private ProjectContext? ProjectOf(string uri)
    {
        string path = PathOf(uri);
        if (!File.Exists(path) && !documents.ContainsKey(uri)) return null;
        string key = Path.GetDirectoryName(path) ?? "";
        ProjectContext? project = projects.GetOrAdd(key, _ => ProjectContext.Find(path, databasesDirectory));
        if (project is null || !project.IsProjectScript(path)) return null;
        string? globalBuffer = documents.TryGetValue(UriOf(project.GlobalPath), out Document? global) ? global.Text : null;
        project.Refresh(globalBuffer);
        return project;
    }

    private JsonNode? WithDocument(JsonNode? parameters, Func<string, List<Token>, int, int, ProjectContext?, JsonNode?> run)
    {
        string uri = parameters!["textDocument"]!["uri"]!.GetValue<string>();
        if (!documents.TryGetValue(uri, out Document? doc)) return null;
        int line = parameters["position"]!["line"]!.GetValue<int>(), column = parameters["position"]!["character"]!.GetValue<int>();
        return run(doc.Text, LuaTokens.Tokenize(doc.Text), line, column, ProjectOf(uri));
    }

    private JsonNode? Definition(JsonNode parameters)
    {
        string uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
        if (!documents.TryGetValue(uri, out Document? doc)) return null;
        List<Token> tokens = LuaTokens.Tokenize(doc.Text);
        ProjectContext? project = ProjectOf(uri);
        (LocalDeclaration? local, LuaGlobalSymbol? global) =
            LuaFeatures.Definition(tokens, parameters["position"]!["line"]!.GetValue<int>(), parameters["position"]!["character"]!.GetValue<int>(), project);
        if (local is not null)
        {
            return new JsonObject { ["uri"] = uri, ["range"] = LuaFeatures.Range(local.Token.Line, local.Token.Column, local.Token.EndLine, local.Token.EndColumn) };
        }
        if (global is not null && project is not null)
        {
            string text = project.GlobalText ?? "";
            string[] lines = text.Replace("\r", "").Split('\n');
            int line = Math.Clamp(global.Line - 1, 0, lines.Length - 1);
            int column = Math.Max(0, lines[line].IndexOf(global.Name, StringComparison.Ordinal));
            return new JsonObject { ["uri"] = UriOf(project.GlobalPath), ["range"] = LuaFeatures.Range(line, column, line, column + global.Name.Length) };
        }
        return null;
    }

    /// <summary>The Lua files of the project's script folder with their text, from the editor's buffer where one is open.</summary>
    private List<(string Uri, string Path, string Text)> ProjectFiles(ProjectContext project)
    {
        var files = new List<(string, string, string)>();
        var openByPath = documents.ToDictionary(pair => Path.GetFullPath(PathOf(pair.Key)), pair => (Uri: pair.Key, pair.Value.Text), StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(project.ScriptsDirectory, "*.lua").Order(StringComparer.Ordinal))
        {
            string full = Path.GetFullPath(path);
            if (openByPath.TryGetValue(full, out (string Uri, string Text) open)) files.Add((open.Uri, full, open.Text));
            else files.Add((UriOf(full), full, File.ReadAllText(full)));
        }
        return files;
    }

    private (NameTarget Target, ProjectContext? Project, List<(string Uri, string Path, string Text)> Files, List<(string File, Token Token)> References)? Locate(JsonNode parameters)
    {
        string uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
        if (!documents.TryGetValue(uri, out Document? doc)) return null;
        ProjectContext? project = ProjectOf(uri);
        List<Token> tokens = LuaTokens.Tokenize(doc.Text);
        NameTarget? target = LuaRefactor.TargetAt(tokens, parameters["position"]!["line"]!.GetValue<int>(), parameters["position"]!["character"]!.GetValue<int>(), project);
        if (target is null) return null;
        List<(string Uri, string Path, string Text)> files = project is null ? [(uri, PathOf(uri), doc.Text)] : ProjectFiles(project);
        if (!files.Any(file => file.Uri == uri)) files.Add((uri, PathOf(uri), doc.Text));
        var tokenized = files.Select(file => (file.Uri, file.Uri == uri ? tokens : LuaTokens.Tokenize(file.Text))).ToList();
        return (target, project, files, LuaRefactor.References(target, uri, tokenized, project));
    }

    private JsonNode? References(JsonNode parameters)
    {
        var located = Locate(parameters);
        var result = new JsonArray();
        if (located is null) return result;
        foreach ((string file, Token token) in located.Value.References)
        {
            result.Add(new JsonObject { ["uri"] = file, ["range"] = LuaFeatures.Range(token.Line, token.Column, token.EndLine, token.EndColumn) });
        }
        return result;
    }

    private JsonNode? PrepareRename(JsonNode parameters)
    {
        string uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
        var located = Locate(parameters);
        if (located is null || !documents.TryGetValue(uri, out Document? doc)) return null;
        if (located.Value.Target.Kind == NameTargetKind.Builtin)
        {
            rpc.Notify("window/showMessage", new JsonObject { ["type"] = 3, ["message"] = $"'{located.Value.Target.Name}' belongs to DSPRE's script language and cannot be renamed." });
            return null;
        }
        List<Token> tokens = LuaTokens.Tokenize(doc.Text);
        int index = LuaFeatures.TokenAt(tokens, parameters["position"]!["line"]!.GetValue<int>(), parameters["position"]!["character"]!.GetValue<int>());
        Token token = tokens[index];
        return new JsonObject { ["range"] = LuaFeatures.Range(token.Line, token.Column, token.EndLine, token.EndColumn), ["placeholder"] = token.Text };
    }

    private JsonNode? Rename(JsonNode parameters)
    {
        var located = Locate(parameters);
        if (located is null) return null;
        string newName = parameters["newName"]!.GetValue<string>();
        var (target, project, files, references) = located.Value;
        string? problem = LuaRefactor.RenameProblem(target, newName, project, files.Select(file => LuaTokens.Tokenize(file.Text)));
        if (problem is not null)
        {
            throw new RequestFailed(problem);
        }

        var changes = new JsonObject();
        foreach (IGrouping<string, (string File, Token Token)> group in references.GroupBy(r => r.File))
        {
            var edits = new JsonArray();
            foreach ((_, Token token) in group)
            {
                edits.Add(new JsonObject { ["range"] = LuaFeatures.Range(token.Line, token.Column, token.EndLine, token.EndColumn), ["newText"] = newName });
            }
            changes[group.Key] = edits;
        }
        return new JsonObject { ["changes"] = changes };
    }

    private JsonNode? Format(JsonNode parameters)
    {
        string uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
        if (!documents.TryGetValue(uri, out Document? doc)) return null;
        JsonNode? options = parameters["options"];
        bool spaces = options?["insertSpaces"]?.GetValue<bool>() ?? false;
        string unit = spaces ? new string(' ', options?["tabSize"]?.GetValue<int>() ?? 4) : "\t";
        string formatted = LuaFormatter.Format(doc.Text, unit);
        if (formatted == doc.Text) return new JsonArray();
        string[] lines = doc.Text.Replace("\r\n", "\n").Split('\n');
        return new JsonArray(new JsonObject
        {
            ["range"] = LuaFeatures.Range(0, 0, lines.Length - 1, lines[^1].Length),
            ["newText"] = formatted,
        });
    }

    private void Changed(string uri)
    {
        if (IsGlobal(uri)) RecheckAll();
        else Schedule(uri);
    }

    private void RecheckAll()
    {
        foreach (string uri in documents.Keys) Schedule(uri);
    }

    private void Schedule(string uri)
    {
        var source = new CancellationTokenSource();
        if (pending.TryGetValue(uri, out CancellationTokenSource? old)) old.Cancel();
        pending[uri] = source;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, source.Token);
                Publish(uri);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"diagnostics {uri}: {ex}");
            }
        });
    }

    private void Publish(string uri)
    {
        if (!documents.TryGetValue(uri, out Document? doc)) return;
        ProjectContext? project = ProjectOf(uri);
        var diagnostics = new JsonArray();
        if (project is not null) Check(uri, doc, project, diagnostics);
        rpc.Notify("textDocument/publishDiagnostics", new JsonObject { ["uri"] = uri, ["version"] = doc.Version, ["diagnostics"] = diagnostics });
    }

    private void Check(string uri, Document doc, ProjectContext project, JsonArray diagnostics)
    {
        string[] lines = doc.Text.Replace("\r", "").Split('\n');
        void Add(int line, string message, int severity)
        {
            int index = Math.Clamp(line - 1, 0, lines.Length - 1);
            string text = lines[index];
            int start = text.Length - text.TrimStart().Length;
            diagnostics.Add(new JsonObject
            {
                ["range"] = LuaFeatures.Range(index, start, index, Math.Max(start, text.TrimEnd().Length)),
                ["severity"] = severity,
                ["source"] = "dspre",
                ["message"] = LinePrefix().Replace(message, ""),
            });
        }

        string fileName = Path.GetFileName(PathOf(uri));
        bool numbered = int.TryParse(Path.GetFileNameWithoutExtension(fileName), out _);
        bool global = IsGlobal(uri);
        try
        {
            if (!numbered && !global)
            {
                LuaParser.Parse(doc.Text);
                return;
            }

            if (project.Language is not { } language)
            {
                if (!warnedAboutDatabase)
                {
                    warnedAboutDatabase = true;
                    rpc.Notify("window/showMessage", new JsonObject
                    {
                        ["type"] = 2,
                        ["message"] = $"DSPRE: no script command database for this project ({project.LanguageError}). Completion of commands and error checking need it; start the server with --databases <folder>.",
                    });
                }
                LuaParser.Parse(doc.Text);
                return;
            }

            // global.lua is checked by running it ahead of an empty script; a script file with global.lua's own text from the buffer.
            LuaCompileResult result = global
                ? LuaScriptCompiler.Compile("", language, doc.Text)
                : LuaScriptCompiler.Compile(doc.Text, language, project.GlobalText);
            foreach (string warning in result.Warnings)
            {
                Match line = LineNumber().Match(warning);
                Add(line.Success ? int.Parse(line.Groups[1].Value) : 1, warning, 2);
            }
        }
        catch (LuaException ex)
        {
            if (ex.InGlobal && !global) Add(1, $"global.lua line {ex.Line - LuaException.GlobalLineBase}: {LinePrefix().Replace(ex.Message, "")}", 1);
            else Add(ex.InGlobal ? ex.Line - LuaException.GlobalLineBase : ex.Line, ex.Message, 1);
        }
        catch (ScriptTextException ex)
        {
            Add(ex.Line, ex.Message, 1);
        }
        catch (InvalidDataException ex)
        {
            Add(1, ex.Message, 1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Add(1, $"The compiler failed on this file: {ex.Message}", 1);
        }
    }

    [GeneratedRegex(@"^Line (\d+):")]
    private static partial Regex LineNumber();
}
