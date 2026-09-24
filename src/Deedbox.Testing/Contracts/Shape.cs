using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Deedbox.Testing.Contracts;

/// <summary>
/// The JSON shape of a type as Deedbox stores it: a primitive kind, an object with members, an array, a map
/// or an enum. It prints as one line, such as <c>{ sku: string, qty: int32 }</c>, and parses back.
/// </summary>
internal sealed record Shape(string Kind, bool Nullable, IReadOnlyList<(string Name, Shape Shape)> Members, Shape? Element)
{
    /// <summary>The JSON name of the subject whose key encrypts this member, when it is [PersonalData].</summary>
    public string? PersonalSubject { get; init; }

    public const string Object = "object";
    public const string Array = "array";
    public const string Map = "map";
    public const string Enum = "enum";

    private static readonly Dictionary<Type, string> Primitives = new()
    {
        [typeof(string)] = "string",
        [typeof(bool)] = "bool",
        [typeof(byte)] = "uint8",
        [typeof(sbyte)] = "int8",
        [typeof(short)] = "int16",
        [typeof(ushort)] = "uint16",
        [typeof(int)] = "int32",
        [typeof(uint)] = "uint32",
        [typeof(long)] = "int64",
        [typeof(ulong)] = "uint64",
        [typeof(float)] = "float32",
        [typeof(double)] = "float64",
        [typeof(decimal)] = "decimal",
        [typeof(char)] = "char",
        [typeof(Guid)] = "uuid",
        [typeof(DateTime)] = "date-time",
        [typeof(DateTimeOffset)] = "date-time",
        [typeof(DateOnly)] = "date",
        [typeof(TimeOnly)] = "time",
        [typeof(TimeSpan)] = "duration",
        [typeof(Uri)] = "uri",
        [typeof(byte[])] = "bytes",
        [typeof(object)] = "any",
        [typeof(JsonElement)] = "any",
    };

    private static Shape Leaf(string kind, bool nullable = false) => new(kind, nullable, [], null);

    public Shape WithNullable(bool nullable) => this with { Nullable = nullable };

    /// <summary>The shape of <paramref name="type"/> under the given JSON options.</summary>
    public static Shape Of(Type type, JsonSerializerOptions options) => Of(type, options, []);

    private static Shape Of(Type type, JsonSerializerOptions options, HashSet<Type> visiting)
    {
        if (System.Nullable.GetUnderlyingType(type) is { } inner)
            return Of(inner, options, visiting).WithNullable(true);
        if (Primitives.TryGetValue(type, out var primitive))
            return Leaf(primitive);
        if (type.IsEnum)
        {
            var members = System.Enum.GetNames(type)
                .Select(n => (n, Leaf(Convert.ToInt64(System.Enum.Parse(type, n), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture))))
                .ToList();
            return new Shape(Enum, false, members, null);
        }

        if (!visiting.Add(type))
            return Leaf("ref(" + type.Name + ")");

        try
        {
            var info = options.GetTypeInfo(type);
            return info.Kind switch
            {
                JsonTypeInfoKind.Object => new Shape(Object, false, MembersOf(info, options, visiting), null),
                JsonTypeInfoKind.Enumerable => new Shape(Array, false, [], Of(ElementType(type, dictionary: false), options, visiting)),
                JsonTypeInfoKind.Dictionary => new Shape(Map, false, [], Of(ElementType(type, dictionary: true), options, visiting)),
                _ => Leaf("custom(" + type.Name + ")"),
            };
        }
        finally
        {
            visiting.Remove(type);
        }
    }

    /// <summary>The item type of a collection, or the value type of a dictionary.</summary>
    private static Type ElementType(Type type, bool dictionary)
    {
        if (type.IsArray)
            return type.GetElementType()!;

        var definition = typeof(IEnumerable<>);
        var enumerable = (type.IsGenericType && type.GetGenericTypeDefinition() == definition ? type : null)
            ?? type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == definition);
        if (enumerable is null)
            return typeof(object);

        var item = enumerable.GetGenericArguments()[0];
        return dictionary && item.IsGenericType && item.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)
            ? item.GetGenericArguments()[1]
            : item;
    }

    private static List<(string, Shape)> MembersOf(JsonTypeInfo info, JsonSerializerOptions options, HashSet<Type> visiting)
    {
        var nullability = new NullabilityInfoContext();
        var members = new List<(string, Shape)>();
        foreach (var property in info.Properties.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var shape = Of(property.PropertyType, options, visiting);
            if (!property.PropertyType.IsValueType && IsNullable(property, nullability))
                shape = shape.WithNullable(true);
            members.Add((property.Name, shape));
        }

        return members;
    }

    private static bool IsNullable(JsonPropertyInfo property, NullabilityInfoContext context) => property.AttributeProvider switch
    {
        PropertyInfo p => context.Create(p).ReadState != NullabilityState.NotNull,
        FieldInfo f => context.Create(f).ReadState != NullabilityState.NotNull,
        _ => true,
    };

    public override string ToString()
    {
        var text = new StringBuilder();
        Write(text);
        return text.ToString();
    }

    private void Write(StringBuilder text)
    {
        switch (Kind)
        {
            case Object:
                text.Append(Members.Count == 0 ? "{ }" : "{ ");
                for (var i = 0; i < Members.Count; i++)
                {
                    if (i > 0)
                        text.Append(", ");
                    text.Append(Quote(Members[i].Name)).Append(": ");
                    Members[i].Shape.Write(text);
                }

                if (Members.Count > 0)
                    text.Append(" }");
                break;
            case Array:
                text.Append('[');
                Element!.Write(text);
                text.Append(']');
                break;
            case Map:
                text.Append("map<");
                Element!.Write(text);
                text.Append('>');
                break;
            case Enum:
                text.Append("enum{").AppendJoin(", ", Members.Select(m => $"{Quote(m.Name)}={m.Shape.Kind}")).Append('}');
                break;
            default:
                text.Append(Kind);
                break;
        }

        if (Nullable)
            text.Append('?');
        if (PersonalSubject is not null)
            text.Append(" pd(").Append(Quote(PersonalSubject)).Append(')');
    }

    private static string Quote(string name) =>
        name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$')
            ? name
            : JsonSerializer.Serialize(name, LockfileJson.Default.String);

    public static Shape Parse(string text)
    {
        var reader = new Reader(text);
        var shape = reader.ReadShape();
        reader.SkipSpaces();
        if (!reader.AtEnd)
            throw reader.Error("unexpected text after the shape");
        return shape;
    }

    private sealed class Reader(string text)
    {
        private int _at;

        public bool AtEnd => _at >= text.Length;

        public Shape ReadShape()
        {
            SkipSpaces();
            Shape shape;
            if (TryTake("{"))
            {
                var members = new List<(string, Shape)>();
                SkipSpaces();
                while (!TryTake("}"))
                {
                    if (members.Count > 0)
                        Expect(",");
                    var name = ReadName();
                    Expect(":");
                    members.Add((name, ReadShape()));
                    SkipSpaces();
                }

                shape = new Shape(Object, false, members, null);
            }
            else if (TryTake("["))
            {
                shape = new Shape(Array, false, [], ReadShape());
                Expect("]");
            }
            else if (TryTake("map<"))
            {
                shape = new Shape(Map, false, [], ReadShape());
                Expect(">");
            }
            else if (TryTake("enum{"))
            {
                var members = new List<(string, Shape)>();
                SkipSpaces();
                while (!TryTake("}"))
                {
                    if (members.Count > 0)
                        Expect(",");
                    var name = ReadName();
                    Expect("=");
                    members.Add((name, Leaf(ReadWord())));
                    SkipSpaces();
                }

                shape = new Shape(Enum, false, members, null);
            }
            else
            {
                shape = Leaf(ReadWord());
            }

            if (TryTake("?"))
                shape = shape.WithNullable(true);
            if (TryTake("pd("))
            {
                shape = shape with { PersonalSubject = ReadName() };
                Expect(")");
            }

            return shape;
        }

        private string ReadName()
        {
            SkipSpaces();
            if (_at < text.Length && text[_at] == '"')
            {
                var start = _at++;
                while (_at < text.Length && text[_at] != '"')
                    _at += text[_at] == '\\' ? 2 : 1;
                _at++;
                return JsonSerializer.Deserialize(text[start.._at], LockfileJson.Default.String)!;
            }

            return ReadWord();
        }

        private string ReadWord()
        {
            SkipSpaces();
            var start = _at;
            var depth = 0;
            while (_at < text.Length)
            {
                var c = text[_at];
                if (c == '(')
                    depth++;
                else if (c == ')' && depth == 0)
                    break;
                else if (c == ')')
                    depth--;
                else if (depth == 0 && (char.IsWhiteSpace(c) || c is ',' or '}' or ']' or '>' or ':' or '=' or '?'))
                    break;
                _at++;
            }

            if (start == _at)
                throw Error("expected a name or type");
            return text[start.._at];
        }

        public void SkipSpaces()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at]))
                _at++;
        }

        private bool TryTake(string token)
        {
            SkipSpaces();
            if (string.CompareOrdinal(text, _at, token, 0, token.Length) != 0)
                return false;
            _at += token.Length;
            return true;
        }

        private void Expect(string token)
        {
            if (!TryTake(token))
                throw Error($"expected '{token}'");
        }

        public FormatException Error(string what) => new($"Lockfile shape '{text}': {what} at column {_at + 1}.");
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class LockfileJson : System.Text.Json.Serialization.JsonSerializerContext;
