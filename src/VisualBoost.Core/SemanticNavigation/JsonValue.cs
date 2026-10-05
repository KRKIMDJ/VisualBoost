using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VisualBoost.Core.SemanticNavigation;

public enum JsonKind
{
    Null,
    Boolean,
    Number,
    String,
    Array,
    Object
}

/// <summary>
/// LSP 메시지에 필요한 만큼만 구현한 불변 JSON 값입니다.
/// netstandard2.0 핵심 계층에 외부 직렬화 의존성을 추가하지 않고, VS 프로세스에 이미 올라온
/// 다른 버전의 JSON 라이브러리와 바인딩 충돌이 생기지 않게 하려는 선택입니다.
/// </summary>
public sealed class JsonValue
{
    private const int MaxDepth = 256;

    public static readonly JsonValue Null = new(JsonKind.Null, null);
    public static readonly JsonValue True = new(JsonKind.Boolean, true);
    public static readonly JsonValue False = new(JsonKind.Boolean, false);

    private static readonly IReadOnlyList<JsonValue> EmptyItems = System.Array.Empty<JsonValue>();
    private static readonly IReadOnlyDictionary<string, JsonValue> EmptyProperties = new Dictionary<string, JsonValue>();

    private readonly object? value;

    private JsonValue(JsonKind kind, object? value)
    {
        Kind = kind;
        this.value = value;
    }

    public JsonKind Kind { get; }

    public bool IsNull => Kind == JsonKind.Null;

    /// <summary>배열이 아니면 빈 목록입니다.</summary>
    public IReadOnlyList<JsonValue> Items => value as IReadOnlyList<JsonValue> ?? EmptyItems;

    /// <summary>객체가 아니면 빈 사전입니다. 같은 이름이 반복되면 마지막 값을 씁니다.</summary>
    public IReadOnlyDictionary<string, JsonValue> Properties => value as IReadOnlyDictionary<string, JsonValue> ?? EmptyProperties;

    /// <summary>없는 속성이나 객체가 아닌 값은 <see cref="Null"/>을 돌려주어 응답 탐색 코드를 단순하게 합니다.</summary>
    public JsonValue this[string name] => Properties.TryGetValue(name, out var item) ? item : Null;

    public string? AsString() => Kind == JsonKind.String ? (string)value! : null;

    public double? AsNumber() => Kind == JsonKind.Number ? (double)value! : null;

    public int? AsInt32()
    {
        var number = AsNumber();
        return number is double d && d >= int.MinValue && d <= int.MaxValue && Math.Floor(d) == d ? (int)d : null;
    }

    public bool? AsBoolean() => Kind == JsonKind.Boolean ? (bool)value! : null;

    public static JsonValue From(string? text) => text is null ? Null : new JsonValue(JsonKind.String, text);

    public static JsonValue From(bool flag) => flag ? True : False;

    public static JsonValue From(double number)
    {
        if (double.IsNaN(number) || double.IsInfinity(number))
        {
            throw new ArgumentOutOfRangeException(nameof(number), "JSON 숫자는 유한해야 합니다.");
        }

        return new JsonValue(JsonKind.Number, number);
    }

    public static JsonValue Array(IEnumerable<JsonValue> items) => new(JsonKind.Array, items.ToArray());

    public static JsonValue Array(params JsonValue[] items) => new(JsonKind.Array, items.ToArray());

    public static JsonValue Object(params (string Name, JsonValue Value)[] properties) =>
        Object(properties.Select(p => new KeyValuePair<string, JsonValue>(p.Name, p.Value)));

    public static JsonValue Object(IEnumerable<KeyValuePair<string, JsonValue>> properties)
    {
        var map = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            map[property.Key] = property.Value ?? Null;
        }

        return new JsonValue(JsonKind.Object, map);
    }

    public static implicit operator JsonValue(string? text) => From(text);

    public static implicit operator JsonValue(bool flag) => From(flag);

    public static implicit operator JsonValue(int number) => From(number);

    public static implicit operator JsonValue(double number) => From(number);

    public override string ToString() => ToJson();

    public string ToJson()
    {
        var builder = new StringBuilder();
        Write(builder, this);
        return builder.ToString();
    }

    public static JsonValue Parse(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var parser = new Parser(text);
        var result = parser.ParseValue(0);
        parser.SkipWhitespace();
        if (!parser.AtEnd)
        {
            throw parser.Error("JSON 값 뒤에 남은 문자가 있습니다");
        }

        return result;
    }

    private static void Write(StringBuilder builder, JsonValue json)
    {
        switch (json.Kind)
        {
            case JsonKind.Null:
                builder.Append("null");
                break;
            case JsonKind.Boolean:
                builder.Append((bool)json.value! ? "true" : "false");
                break;
            case JsonKind.Number:
                var number = (double)json.value!;
                builder.Append(Math.Floor(number) == number && Math.Abs(number) < 1e15
                    ? ((long)number).ToString(CultureInfo.InvariantCulture)
                    : number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case JsonKind.String:
                WriteString(builder, (string)json.value!);
                break;
            case JsonKind.Array:
                builder.Append('[');
                var first = true;
                foreach (var item in json.Items)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    Write(builder, item);
                }

                builder.Append(']');
                break;
            default:
                builder.Append('{');
                var firstProperty = true;
                foreach (var property in json.Properties)
                {
                    if (!firstProperty) builder.Append(',');
                    firstProperty = false;
                    WriteString(builder, property.Key);
                    builder.Append(':');
                    Write(builder, property.Value);
                }

                builder.Append('}');
                break;
        }
    }

    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        // 비ASCII 문자는 UTF-8 본문에 그대로 둡니다. 대리 쌍도 순서대로 기록됩니다.
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    private sealed class Parser
    {
        private readonly string text;
        private int position;

        public Parser(string text) => this.text = text;

        public bool AtEnd => position >= text.Length;

        public FormatException Error(string message) => new($"{message} (위치 {position})");

        public void SkipWhitespace()
        {
            while (position < text.Length && (text[position] == ' ' || text[position] == '\t' || text[position] == '\n' || text[position] == '\r'))
            {
                position++;
            }
        }

        public JsonValue ParseValue(int depth)
        {
            if (depth > MaxDepth)
            {
                throw Error("JSON 중첩이 너무 깊습니다");
            }

            SkipWhitespace();
            if (AtEnd)
            {
                throw Error("JSON 값이 끝났습니다");
            }

            switch (text[position])
            {
                case '{': return ParseObject(depth);
                case '[': return ParseArray(depth);
                case '"': return new JsonValue(JsonKind.String, ParseString());
                case 't': Expect("true"); return True;
                case 'f': Expect("false"); return False;
                case 'n': Expect("null"); return Null;
                default: return ParseNumber();
            }
        }

        private void Expect(string literal)
        {
            if (string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0)
            {
                throw Error("알 수 없는 JSON 토큰입니다");
            }

            position += literal.Length;
        }

        private JsonValue ParseObject(int depth)
        {
            position++;
            var map = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            SkipWhitespace();
            if (!AtEnd && text[position] == '}')
            {
                position++;
                return new JsonValue(JsonKind.Object, map);
            }

            while (true)
            {
                SkipWhitespace();
                if (AtEnd || text[position] != '"')
                {
                    throw Error("객체 속성 이름이 필요합니다");
                }

                var name = ParseString();
                SkipWhitespace();
                if (AtEnd || text[position] != ':')
                {
                    throw Error("':'가 필요합니다");
                }

                position++;
                map[name] = ParseValue(depth + 1);
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Error("객체가 닫히지 않았습니다");
                }

                if (text[position] == ',')
                {
                    position++;
                    continue;
                }

                if (text[position] == '}')
                {
                    position++;
                    return new JsonValue(JsonKind.Object, map);
                }

                throw Error("',' 또는 '}'가 필요합니다");
            }
        }

        private JsonValue ParseArray(int depth)
        {
            position++;
            var items = new List<JsonValue>();
            SkipWhitespace();
            if (!AtEnd && text[position] == ']')
            {
                position++;
                return new JsonValue(JsonKind.Array, items.ToArray());
            }

            while (true)
            {
                items.Add(ParseValue(depth + 1));
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Error("배열이 닫히지 않았습니다");
                }

                if (text[position] == ',')
                {
                    position++;
                    continue;
                }

                if (text[position] == ']')
                {
                    position++;
                    return new JsonValue(JsonKind.Array, items.ToArray());
                }

                throw Error("',' 또는 ']'가 필요합니다");
            }
        }

        private string ParseString()
        {
            position++;
            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw Error("문자열이 닫히지 않았습니다");
                }

                var c = text[position++];
                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c != '\\')
                {
                    if (c < 0x20)
                    {
                        throw Error("문자열에 제어 문자가 있습니다");
                    }

                    builder.Append(c);
                    continue;
                }

                if (AtEnd)
                {
                    throw Error("이스케이프가 끝났습니다");
                }

                var escape = text[position++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (position + 4 > text.Length ||
                            !int.TryParse(text.Substring(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                        {
                            throw Error("잘못된 \\u 이스케이프입니다");
                        }

                        position += 4;
                        builder.Append((char)code);
                        break;
                    default:
                        throw Error("알 수 없는 이스케이프입니다");
                }
            }
        }

        private JsonValue ParseNumber()
        {
            var start = position;
            if (text[position] == '-') position++;
            while (position < text.Length && (char.IsDigit(text[position]) || text[position] is '.' or 'e' or 'E' or '+' or '-'))
            {
                position++;
            }

            if (start == position ||
                !double.TryParse(text.Substring(start, position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                position = start;
                throw Error("잘못된 JSON 숫자입니다");
            }

            return new JsonValue(JsonKind.Number, number);
        }
    }
}
