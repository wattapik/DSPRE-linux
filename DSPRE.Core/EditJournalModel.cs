using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DSPRE.Core;

public enum JournalOpVerb
{
    Set,
    Add,
    Remove
}

/// <summary>One change to one document, addressed by a path such as <c>Overworlds[2].MapPositionX</c>,
/// <c>[5]</c> (root list element), <c>Types[4][7]</c> (2D array cell) or <c>Rates[Old Rod]</c>
/// (dictionary entry). An empty path addresses the document root.</summary>
public sealed record JournalOp(JournalOpVerb Verb, string Path, JsonNode? Value);

/// <summary>Reflection-based bridge between the Core record types and JSON: snapshots any record graph
/// to a JsonNode, diffs two snapshots into Set/Add/Remove ops, and applies those ops back onto a freshly
/// loaded record. Being generic over public members is what lets every editor's save path be journaled
/// without hand-writing a serializer per record type - a new field on a record is picked up
/// automatically.</summary>
public static class EditJournalModel
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    // Beyond this many cells the LCS table gets too large to be worth it for a journal; a positional
    // diff is still correct, only less minimal.
    private const long MaxLcsCells = 4_000_000;

    private sealed record Member(string Name, Type Type, Func<object, object?> Get, Action<object, object?> Set);

    private static readonly ConcurrentDictionary<Type, Member[]> MemberCache = new();

    #region Type classification

    private static bool IsScalar(Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(string) || underlying == typeof(decimal);
    }

    private static Type? GetListElementType(Type type)
    {
        if (type.IsArray) return null;
        Type? list = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>));
        return list?.GetGenericArguments()[0];
    }

    private static Type? GetSetElementType(Type type) =>
        type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISet<>))?.GetGenericArguments()[0];

    private static Type? GetDictionaryValueType(Type type)
    {
        Type? dictionary = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDictionary<,>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        return dictionary is not null && dictionary.GetGenericArguments()[0] == typeof(string) ? dictionary.GetGenericArguments()[1] : null;
    }

    private static Member[] GetMembers(Type type) => MemberCache.GetOrAdd(type, static type =>
    {
        var hierarchy = new List<Type>();
        for (Type? current = type; current is not null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
        {
            hierarchy.Insert(0, current);
        }

        var members = new List<Member>();
        foreach (Type declaring in hierarchy)
        {
            const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (FieldInfo field in declaring.GetFields(Declared).OrderBy(field => field.MetadataToken))
            {
                if (field.IsLiteral) continue;
                members.Add(new Member(field.Name, field.FieldType, field.GetValue, field.SetValue));
            }

            foreach (PropertyInfo property in declaring.GetProperties(Declared).OrderBy(property => property.MetadataToken))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                MethodInfo? getter = property.GetGetMethod();
                if (getter is null || getter.IsAbstract) continue;

                // Properties with neither a setter nor a compiler backing field are computed views over
                // other members (IsEmpty, Width, Kind...) and are not state of their own.
                MethodInfo? setter = property.GetSetMethod(nonPublic: true);
                FieldInfo? backingField = declaring.GetField($"<{property.Name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                if (setter is null && backingField is null) continue;

                Action<object, object?> set = setter is not null
                    ? (target, value) => setter.Invoke(target, [value])
                    : backingField!.SetValue;
                members.Add(new Member(property.Name, property.PropertyType, property.GetValue, set));
            }
        }

        return members.ToArray();
    });

    #endregion

    #region Snapshot / construction

    public static JsonNode? ToNode(object? value)
    {
        if (value is null) return null;
        Type type = value.GetType();

        if (type.IsEnum) return JsonValue.Create(value.ToString());
        if (IsScalar(type)) return JsonSerializer.SerializeToNode(value, type, JsonOptions);

        if (value is Array { Rank: 2 } grid)
        {
            var rows = new JsonArray();
            for (int r = 0; r < grid.GetLength(0); r++)
            {
                var row = new JsonArray();
                for (int c = 0; c < grid.GetLength(1); c++) row.Add(ToNode(grid.GetValue(r, c)));
                rows.Add(row);
            }
            return rows;
        }

        if (GetDictionaryValueType(type) is not null && value is IDictionary dictionary)
        {
            var result = new JsonObject();
            foreach (DictionaryEntry entry in dictionary) result[(string)entry.Key] = ToNode(entry.Value);
            return result;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            var array = new JsonArray();
            foreach (object? element in enumerable) array.Add(ToNode(element));
            return array;
        }

        var obj = new JsonObject();
        foreach (Member member in GetMembers(type)) obj[member.Name] = ToNode(member.Get(value));
        return obj;
    }

    public static object? FromNode(JsonNode? node, Type type)
    {
        if (node is null)
        {
            return type.IsValueType && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null;
        }

        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsEnum)
        {
            return node.GetValueKind() == JsonValueKind.String
                ? Enum.Parse(underlying, node.GetValue<string>())
                : Enum.ToObject(underlying, node.GetValue<long>());
        }

        if (IsScalar(underlying)) return node.Deserialize(underlying, JsonOptions);

        if (type.IsArray && type.GetArrayRank() == 2)
        {
            Type element = type.GetElementType()!;
            JsonArray rows = node.AsArray();
            int height = rows.Count;
            int width = height == 0 ? 0 : rows[0]!.AsArray().Count;
            Array grid = Array.CreateInstance(element, height, width);
            for (int r = 0; r < height; r++)
            {
                JsonArray row = rows[r]!.AsArray();
                for (int c = 0; c < width; c++) grid.SetValue(FromNode(row[c], element), r, c);
            }
            return grid;
        }

        if (type.IsArray)
        {
            Type element = type.GetElementType()!;
            JsonArray items = node.AsArray();
            Array array = Array.CreateInstance(element, items.Count);
            for (int i = 0; i < items.Count; i++) array.SetValue(FromNode(items[i], element), i);
            return array;
        }

        if (GetDictionaryValueType(type) is Type dictionaryValue)
        {
            var dictionary = (IDictionary)Activator.CreateInstance(type)!;
            foreach ((string key, JsonNode? value) in node.AsObject()) dictionary[key] = FromNode(value, dictionaryValue);
            return dictionary;
        }

        if (GetSetElementType(type) is Type setElement)
        {
            object set = Activator.CreateInstance(type)!;
            MethodInfo add = type.GetMethod("Add", [setElement])!;
            foreach (JsonNode? item in node.AsArray()) add.Invoke(set, [FromNode(item, setElement)]);
            return set;
        }

        if (GetListElementType(type) is Type listElement)
        {
            var list = (IList)Activator.CreateInstance(type)!;
            foreach (JsonNode? item in node.AsArray()) list.Add(FromNode(item, listElement));
            return list;
        }

        return ConstructObject(node.AsObject(), type);
    }

    private static object ConstructObject(JsonObject node, Type type)
    {
        object instance;
        ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        ConstructorInfo? parameterless = constructors.FirstOrDefault(ctor => ctor.GetParameters().Length == 0);
        if (parameterless is not null || type.IsValueType)
        {
            instance = parameterless is not null ? parameterless.Invoke([]) : Activator.CreateInstance(type)!;
        }
        else
        {
            // Primary-constructor types (HoneyTreeGroup(name, description), LevelScriptFile(id)...):
            // feed constructor parameters from same-named members, then set the rest below.
            ConstructorInfo ctor = constructors.OrderByDescending(ctor => ctor.GetParameters().Length).First();
            object?[] arguments = ctor.GetParameters()
                .Select(parameter =>
                {
                    JsonNode? value = node.FirstOrDefault(pair => string.Equals(pair.Key, parameter.Name, StringComparison.OrdinalIgnoreCase)).Value;
                    return FromNode(value, parameter.ParameterType);
                })
                .ToArray();
            instance = ctor.Invoke(arguments);
        }

        foreach (Member member in GetMembers(type))
        {
            if (node.TryGetPropertyValue(member.Name, out JsonNode? value))
            {
                member.Set(instance, FromNode(value, member.Type));
            }
        }

        return instance;
    }

    #endregion

    #region Diff

    public static List<JournalOp> Diff(Type type, JsonNode? before, JsonNode? after)
    {
        var ops = new List<JournalOp>();
        Diff(type, before, after, string.Empty, ops);
        return ops;
    }

    private static string Canonical(JsonNode? node) => node?.ToJsonString(JsonOptions) ?? "null";

    private static void Diff(Type type, JsonNode? before, JsonNode? after, string path, List<JournalOp> ops)
    {
        if (Canonical(before) == Canonical(after)) return;
        if (before is null || after is null || IsScalar(type) || GetSetElementType(type) is not null)
        {
            ops.Add(new JournalOp(JournalOpVerb.Set, path, after?.DeepClone()));
            return;
        }

        if (type.IsArray && type.GetArrayRank() == 2)
        {
            DiffGrid(type.GetElementType()!, before.AsArray(), after.AsArray(), path, ops);
            return;
        }

        if (type.IsArray)
        {
            JsonArray a = before.AsArray(), b = after.AsArray();
            if (a.Count != b.Count)
            {
                ops.Add(new JournalOp(JournalOpVerb.Set, path, after.DeepClone()));
                return;
            }
            for (int i = 0; i < a.Count; i++) Diff(type.GetElementType()!, a[i], b[i], $"{path}[{i}]", ops);
            return;
        }

        if (GetDictionaryValueType(type) is Type dictionaryValue)
        {
            JsonObject a = before.AsObject(), b = after.AsObject();
            if (a.Select(pair => pair.Key).Except(b.Select(pair => pair.Key)).Any())
            {
                ops.Add(new JournalOp(JournalOpVerb.Set, path, after.DeepClone()));
                return;
            }
            foreach ((string key, JsonNode? value) in b) Diff(dictionaryValue, a[key], value, $"{path}[{key}]", ops);
            return;
        }

        if (GetListElementType(type) is Type listElement)
        {
            DiffList(listElement, before.AsArray(), after.AsArray(), path, ops);
            return;
        }

        JsonObject beforeObject = before.AsObject(), afterObject = after.AsObject();
        foreach (Member member in GetMembers(type))
        {
            Diff(member.Type, beforeObject[member.Name], afterObject[member.Name], JoinMember(path, member.Name), ops);
        }
    }

    private static string JoinMember(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private static void DiffGrid(Type elementType, JsonArray before, JsonArray after, string path, List<JournalOp> ops)
    {
        int width = before.Count == 0 ? 0 : before[0]!.AsArray().Count;
        if (before.Count != after.Count || (after.Count > 0 && after[0]!.AsArray().Count != width))
        {
            ops.Add(new JournalOp(JournalOpVerb.Set, path, after.DeepClone()));
            return;
        }

        for (int r = 0; r < after.Count; r++)
        {
            JsonArray a = before[r]!.AsArray(), b = after[r]!.AsArray();
            var changed = Enumerable.Range(0, width).Where(c => Canonical(a[c]) != Canonical(b[c])).ToList();
            if (changed.Count == 0) continue;
            // A painted/filled row reads better (and replays identically) as one row write.
            if (changed.Count > width / 2)
            {
                ops.Add(new JournalOp(JournalOpVerb.Set, $"{path}[{r}]", b.DeepClone()));
                continue;
            }
            foreach (int c in changed) ops.Add(new JournalOp(JournalOpVerb.Set, $"{path}[{r}][{c}]", b[c]?.DeepClone()));
        }
    }

    private static void DiffList(Type elementType, JsonArray before, JsonArray after, string path, List<JournalOp> ops)
    {
        var positional = new List<JournalOp>();
        int common = Math.Min(before.Count, after.Count);
        for (int i = 0; i < common; i++) Diff(elementType, before[i], after[i], $"{path}[{i}]", positional);
        for (int i = before.Count - 1; i >= common; i--) positional.Add(new JournalOp(JournalOpVerb.Remove, $"{path}[{i}]", null));
        for (int i = common; i < after.Count; i++) positional.Add(new JournalOp(JournalOpVerb.Add, $"{path}[{i}]", after[i]?.DeepClone()));

        List<JournalOp>? aligned = DiffListAligned(elementType, before, after, path);
        ops.AddRange(aligned is not null && aligned.Count < positional.Count ? aligned : positional);
    }

    /// <summary>LCS-aligned list diff, so deleting or inserting one element in the middle of a list is
    /// journaled as one Remove/Add instead of a Set on every element after it. Ops are emitted against
    /// the list as it evolves, so replaying them in order reproduces the new list exactly.</summary>
    private static List<JournalOp>? DiffListAligned(Type elementType, JsonArray before, JsonArray after, string path)
    {
        string[] a = before.Select(Canonical).ToArray();
        string[] b = after.Select(Canonical).ToArray();
        IReadOnlyList<(int OldIndex, int NewIndex)>? matches = LongestCommonSubsequence(a, b);
        if (matches is null) return null;

        var ops = new List<JournalOp>();
        int position = 0, oldCursor = 0, newCursor = 0;
        foreach ((int oldIndex, int newIndex) in matches.Append((a.Length, b.Length)))
        {
            int removed = oldIndex - oldCursor, inserted = newIndex - newCursor, paired = Math.Min(removed, inserted);
            for (int k = 0; k < paired; k++, position++)
            {
                Diff(elementType, before[oldCursor + k], after[newCursor + k], $"{path}[{position}]", ops);
            }
            for (int k = paired; k < removed; k++) ops.Add(new JournalOp(JournalOpVerb.Remove, $"{path}[{position}]", null));
            for (int k = paired; k < inserted; k++, position++) ops.Add(new JournalOp(JournalOpVerb.Add, $"{path}[{position}]", after[newCursor + k]?.DeepClone()));

            oldCursor = oldIndex + 1;
            newCursor = newIndex + 1;
            position++;
        }
        return ops;
    }

    /// <summary>Matching (oldIndex, newIndex) pairs of a longest common subsequence, in order, or null
    /// when the differing middle section is too large to align.</summary>
    public static IReadOnlyList<(int OldIndex, int NewIndex)>? LongestCommonSubsequence(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix]) suffix++;

        int n = a.Count - prefix - suffix, m = b.Count - prefix - suffix;
        if ((long)(n + 1) * (m + 1) > MaxLcsCells) return null;

        var lengths = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lengths[i, j] = a[prefix + i] == b[prefix + j]
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var matches = new List<(int, int)>();
        for (int k = 0; k < prefix; k++) matches.Add((k, k));
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[prefix + x] == b[prefix + y]) { matches.Add((prefix + x, prefix + y)); x++; y++; }
            else if (lengths[x + 1, y] >= lengths[x, y + 1]) x++;
            else y++;
        }
        for (int k = 0; k < suffix; k++) matches.Add((a.Count - suffix + k, b.Count - suffix + k));
        return matches;
    }

    #endregion

    #region Apply

    private abstract record PathSegment;
    private sealed record MemberSegment(string Name) : PathSegment;
    private sealed record IndexSegment(string Key) : PathSegment
    {
        public int Index => int.TryParse(Key, out int index) ? index : throw new FormatException($"'{Key}' is not a list index.");
    }

    private static List<PathSegment> ParsePath(string path)
    {
        var segments = new List<PathSegment>();
        int i = 0;
        while (i < path.Length)
        {
            if (path[i] == '.') { i++; continue; }
            if (path[i] == '[')
            {
                int end = path.IndexOf(']', i);
                if (end < 0) throw new FormatException($"Unterminated '[' in path '{path}'.");
                segments.Add(new IndexSegment(path[(i + 1)..end]));
                i = end + 1;
                continue;
            }
            int stop = path.IndexOfAny(['.', '['], i);
            if (stop < 0) stop = path.Length;
            segments.Add(new MemberSegment(path[i..stop]));
            i = stop;
        }
        return segments;
    }

    /// <summary>Applies one op to a document root and returns the (possibly replaced) root.</summary>
    public static object? Apply(object? root, Type rootType, JournalOp op)
    {
        List<PathSegment> segments = ParsePath(op.Path);
        return ApplyAt(root, rootType, segments, 0, op);
    }

    private static object? ApplyAt(object? current, Type declaredType, List<PathSegment> segments, int index, JournalOp op)
    {
        if (index == segments.Count)
        {
            if (op.Verb != JournalOpVerb.Set) throw new InvalidOperationException($"{op.Verb} needs a list element path, got '{op.Path}'.");
            return FromNode(op.Value, declaredType);
        }

        if (current is null) throw new InvalidOperationException($"Path '{op.Path}' goes through a null value.");
        Type type = current.GetType();
        PathSegment segment = segments[index];
        bool last = index == segments.Count - 1;

        if (segment is MemberSegment memberSegment)
        {
            Member member = GetMembers(type).FirstOrDefault(m => m.Name == memberSegment.Name)
                ?? throw new InvalidOperationException($"{type.Name} has no member '{memberSegment.Name}' (path '{op.Path}').");
            object? updated = ApplyAt(member.Get(current), member.Type, segments, index + 1, op);
            member.Set(current, updated);
            return current;
        }

        var indexSegment = (IndexSegment)segment;

        if (current is Array { Rank: 2 } grid)
        {
            Type element = type.GetElementType()!;
            int row = indexSegment.Index;
            if (last)
            {
                JsonArray values = op.Value?.AsArray() ?? throw new InvalidOperationException($"Row write '{op.Path}' needs an array value.");
                for (int c = 0; c < grid.GetLength(1); c++) grid.SetValue(FromNode(values[c], element), row, c);
                return current;
            }
            int column = ((IndexSegment)segments[index + 1]).Index;
            grid.SetValue(ApplyAt(grid.GetValue(row, column), element, segments, index + 2, op), row, column);
            return current;
        }

        if (GetDictionaryValueType(type) is Type dictionaryValue && current is IDictionary dictionary)
        {
            dictionary[indexSegment.Key] = ApplyAt(dictionary[indexSegment.Key], dictionaryValue, segments, index + 1, op);
            return current;
        }

        if (current is IList list)
        {
            Type element = type.IsArray ? type.GetElementType()! : GetListElementType(type)!;
            int position = indexSegment.Index;
            if (last && op.Verb == JournalOpVerb.Add)
            {
                list.Insert(position, FromNode(op.Value, element));
                return current;
            }
            if (last && op.Verb == JournalOpVerb.Remove)
            {
                list.RemoveAt(position);
                return current;
            }
            list[position] = ApplyAt(list[position], element, segments, index + 1, op);
            return current;
        }

        throw new InvalidOperationException($"Cannot index into {type.Name} (path '{op.Path}').");
    }

    #endregion
}
