using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityTranslator.Core.Cheats;

/// <summary>
/// 最小 JSON 读写器，只覆盖作弊消息用到的类型（对象、字符串、数组、布尔、整数）。
///
/// 为什么不用 System.Text.Json / Newtonsoft：这份文件同时被链接进
/// net8.0 的 Core 与 netstandard2.1 的 BepInEx 插件。System.Text.Json 在 Unity 的
/// netstandard2.1 里需要额外 NuGet 依赖，Newtonsoft 的版本又由游戏决定；
/// 两者都会给游戏进程带来加载失败的风险，因此这里只使用 System.Text 与 BCL。
/// </summary>
internal static class MiniJson
{
    public static string WriteString(string? value)
    {
        if (value is null) return "null";
        var builder = new StringBuilder(value.Length + 2);
        WriteString(builder, value);
        return builder.ToString();
    }

    public static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < ' ')
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(character);
                    break;
            }
        }
        builder.Append('"');
    }

    public static string WriteInt(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string WriteBool(bool value) => value ? "true" : "false";

    /// <summary>把字符串写成 JSON 字符串，null 写成空字符串（作弊消息里不接受 null 文本）。</summary>
    public static string Text(string? value) => WriteString(value ?? string.Empty);

    public static bool TryParse(string? json, out MiniJsonValue value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(json)) return false;
        var reader = new Reader(json!);
        if (!reader.TryReadValue(out var parsed)) return false;
        reader.SkipWhitespace();
        // 只接受单条完整值，尾部多余内容一律视为畸形消息。
        if (!reader.AtEnd) return false;
        value = parsed;
        return true;
    }

    private struct Reader
    {
        private readonly string _text;
        private int _index;

        public Reader(string text)
        {
            _text = text;
            _index = 0;
        }

        public bool AtEnd
        {
            get
            {
                SkipWhitespace();
                return _index >= _text.Length;
            }
        }

        public void SkipWhitespace()
        {
            while (_index < _text.Length)
            {
                var character = _text[_index];
                if (character == ' ' || character == '\t' || character == '\n' || character == '\r')
                {
                    _index++;
                    continue;
                }
                break;
            }
        }

        public bool TryReadValue(out MiniJsonValue value)
        {
            value = default;
            SkipWhitespace();
            if (_index >= _text.Length) return false;
            switch (_text[_index])
            {
                case '{': return TryReadObject(out value);
                case '[': return TryReadArray(out value);
                case '"':
                    if (!TryReadString(out var text)) return false;
                    value = MiniJsonValue.FromString(text);
                    return true;
                case 't':
                    if (!Expect("true")) return false;
                    value = MiniJsonValue.FromBoolean(true);
                    return true;
                case 'f':
                    if (!Expect("false")) return false;
                    value = MiniJsonValue.FromBoolean(false);
                    return true;
                case 'n':
                    if (!Expect("null")) return false;
                    value = MiniJsonValue.Null;
                    return true;
                default: return TryReadNumber(out value);
            }
        }

        private bool TryReadObject(out MiniJsonValue value)
        {
            value = default;
            var members = new List<KeyValuePair<string, MiniJsonValue>>();
            _index++; // '{'
            SkipWhitespace();
            if (_index < _text.Length && _text[_index] == '}')
            {
                _index++;
                value = MiniJsonValue.FromObject(members);
                return true;
            }
            while (true)
            {
                SkipWhitespace();
                if (!TryReadString(out var name)) return false;
                SkipWhitespace();
                if (_index >= _text.Length || _text[_index] != ':') return false;
                _index++;
                if (!TryReadValue(out var member)) return false;
                members.Add(new KeyValuePair<string, MiniJsonValue>(name, member));
                SkipWhitespace();
                if (_index >= _text.Length) return false;
                if (_text[_index] == ',') { _index++; continue; }
                if (_text[_index] == '}') { _index++; break; }
                return false;
            }
            value = MiniJsonValue.FromObject(members);
            return true;
        }

        private bool TryReadArray(out MiniJsonValue value)
        {
            value = default;
            var items = new List<MiniJsonValue>();
            _index++; // '['
            SkipWhitespace();
            if (_index < _text.Length && _text[_index] == ']')
            {
                _index++;
                value = MiniJsonValue.FromArray(items);
                return true;
            }
            while (true)
            {
                if (!TryReadValue(out var item)) return false;
                items.Add(item);
                SkipWhitespace();
                if (_index >= _text.Length) return false;
                if (_text[_index] == ',') { _index++; continue; }
                if (_text[_index] == ']') { _index++; break; }
                return false;
            }
            value = MiniJsonValue.FromArray(items);
            return true;
        }

        private bool TryReadString(out string value)
        {
            value = string.Empty;
            if (_index >= _text.Length || _text[_index] != '"') return false;
            _index++;
            var builder = new StringBuilder();
            while (_index < _text.Length)
            {
                var character = _text[_index++];
                if (character == '"')
                {
                    value = builder.ToString();
                    return true;
                }
                if (character != '\\')
                {
                    builder.Append(character);
                    continue;
                }
                if (_index >= _text.Length) return false;
                var escape = _text[_index++];
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
                        if (_index + 4 > _text.Length) return false;
                        if (!ushort.TryParse(_text.Substring(_index, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out var code)) return false;
                        _index += 4;
                        builder.Append((char)code);
                        break;
                    default: return false;
                }
            }
            return false;
        }

        private bool TryReadNumber(out MiniJsonValue value)
        {
            value = default;
            var start = _index;
            if (_index < _text.Length && (_text[_index] == '-' || _text[_index] == '+')) _index++;
            while (_index < _text.Length && (IsDigit(_text[_index]) ||
                   _text[_index] == '.' || _text[_index] == 'e' || _text[_index] == 'E' ||
                   _text[_index] == '+' || _text[_index] == '-'))
                _index++;
            if (_index == start) return false;
            var span = _text.Substring(start, _index - start);
            if (long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            {
                value = MiniJsonValue.FromNumber(integer);
                return true;
            }
            if (double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
            {
                value = MiniJsonValue.FromNumber(real);
                return true;
            }
            return false;
        }

        private bool Expect(string literal)
        {
            if (_index + literal.Length > _text.Length) return false;
            if (string.CompareOrdinal(_text, _index, literal, 0, literal.Length) != 0) return false;
            _index += literal.Length;
            return true;
        }

        private static bool IsDigit(char character) => character >= '0' && character <= '9';
    }
}

internal enum MiniJsonType
{
    Null,
    Boolean,
    Number,
    String,
    Array,
    Object
}

/// <summary>不可变的最小 JSON 值。只提供作弊消息用得到的读取方式。</summary>
internal readonly struct MiniJsonValue
{
    private readonly object? _value;

    private MiniJsonValue(MiniJsonType type, object? value)
    {
        Type = type;
        _value = value;
    }

    public MiniJsonType Type { get; }

    public static MiniJsonValue Null => new MiniJsonValue(MiniJsonType.Null, null);
    public static MiniJsonValue FromBoolean(bool value) => new MiniJsonValue(MiniJsonType.Boolean, value);
    public static MiniJsonValue FromNumber(long value) => new MiniJsonValue(MiniJsonType.Number, value);
    public static MiniJsonValue FromNumber(double value) => new MiniJsonValue(MiniJsonType.Number, value);
    public static MiniJsonValue FromString(string value) => new MiniJsonValue(MiniJsonType.String, value);
    public static MiniJsonValue FromArray(List<MiniJsonValue> items) => new MiniJsonValue(MiniJsonType.Array, items);
    public static MiniJsonValue FromObject(List<KeyValuePair<string, MiniJsonValue>> members) =>
        new MiniJsonValue(MiniJsonType.Object, members);

    public bool IsObject => Type == MiniJsonType.Object;

    public bool IsArray => Type == MiniJsonType.Array;

    public IReadOnlyList<MiniJsonValue> Items =>
        _value as List<MiniJsonValue> ?? (IReadOnlyList<MiniJsonValue>)Array.Empty<MiniJsonValue>();

    public bool TryGet(string name, out MiniJsonValue value)
    {
        value = default;
        if (!(_value is List<KeyValuePair<string, MiniJsonValue>> members)) return false;
        foreach (var member in members)
        {
            if (!string.Equals(member.Key, name, StringComparison.Ordinal)) continue;
            if (member.Value.Type == MiniJsonType.Null) return false;
            value = member.Value;
            return true;
        }
        return false;
    }

    public bool TryGetString(string name, out string value)
    {
        value = string.Empty;
        if (!TryGet(name, out var member) || member.Type != MiniJsonType.String) return false;
        value = (string)member._value!;
        return true;
    }

    public bool TryGetInt32(string name, out int value)
    {
        value = 0;
        if (!TryGet(name, out var member) || member.Type != MiniJsonType.Number) return false;
        if (member._value is long integer && integer >= int.MinValue && integer <= int.MaxValue)
        {
            value = (int)integer;
            return true;
        }
        if (member._value is double real && real >= int.MinValue && real <= int.MaxValue)
        {
            value = (int)real;
            return true;
        }
        return false;
    }

    public bool TryGetBoolean(string name, out bool value)
    {
        value = false;
        if (!TryGet(name, out var member) || member.Type != MiniJsonType.Boolean) return false;
        value = (bool)member._value!;
        return true;
    }
}
