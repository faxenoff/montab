using System.Globalization;
using System.Text;

namespace Montab.Config;

/// <summary>
/// Последовательный разбор JSON-текста без промежуточного дерева: вызывающий сам
/// идёт по структуре и читает значения ожидаемого типа. Любое нарушение формата —
/// FormatException. Заменяет System.Text.Json, который весил ~15% exe.
/// </summary>
internal ref struct JsonReader
{
    readonly ReadOnlySpan<char> _text;
    int _pos;

    public JsonReader(ReadOnlySpan<char> text)
    {
        _text = text;
        _pos = 0;
    }

    /// <summary>Следующий значащий символ без сдвига; '\0' — текст кончился.</summary>
    char Peek()
    {
        while (_pos < _text.Length && _text[_pos] is ' ' or '\t' or '\r' or '\n')
            _pos++;
        return _pos < _text.Length ? _text[_pos] : '\0';
    }

    bool TryConsume(char c)
    {
        if (Peek() != c)
            return false;
        _pos++;
        return true;
    }

    public void Expect(char c)
    {
        if (!TryConsume(c))
            throw new FormatException();
    }

    /// <summary>
    /// Шаг по элементам объекта или массива: false — встретилась закрывающая скобка
    /// <paramref name="close"/> (она уже прочитана), true — дальше идёт очередной элемент.
    /// </summary>
    public bool NextElement(char close, ref bool first)
    {
        if (TryConsume(close))
            return false;
        if (!first)
            Expect(',');
        first = false;
        return true;
    }

    public void ExpectEnd()
    {
        if (Peek() != '\0')
            throw new FormatException();
    }

    public string ReadPropertyName()
    {
        string name = ReadString();
        Expect(':');
        return name;
    }

    public string ReadString()
    {
        Expect('"');
        var rest = _text[_pos..];
        int end = rest.IndexOfAny('"', '\\');
        if (end < 0)
            throw new FormatException();
        if (rest[end] == '"')
        {
            // Обычный случай: экранирования нет, строка — готовый срез
            _pos += end + 1;
            return new string(rest[..end]);
        }

        var sb = new StringBuilder(rest.Length);
        while (true)
        {
            if (_pos >= _text.Length)
                throw new FormatException();
            char c = _text[_pos++];
            if (c == '"')
                return sb.ToString();
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            if (_pos >= _text.Length)
                throw new FormatException();
            c = _text[_pos++];
            switch (c)
            {
                case '"' or '\\' or '/': sb.Append(c); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u': sb.Append(ReadHex4()); break;
                default: throw new FormatException();
            }
        }
    }

    char ReadHex4()
    {
        if (_pos + 4 > _text.Length)
            throw new FormatException();
        int value = 0;
        for (int i = 0; i < 4; i++)
        {
            char c = _text[_pos++];
            int digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => throw new FormatException(),
            };
            value = value << 4 | digit;
        }
        return (char)value;
    }

    public double ReadNumber()
    {
        Peek();
        int start = _pos;
        while (_pos < _text.Length && _text[_pos] is >= '0' and <= '9' or '-' or '+' or '.' or 'e' or 'E')
            _pos++;
        return double.Parse(_text[start.._pos], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    public bool ReadBool()
    {
        Peek();
        var rest = _text[_pos..];
        if (rest.StartsWith("true"))
        {
            _pos += 4;
            return true;
        }
        if (rest.StartsWith("false"))
        {
            _pos += 5;
            return false;
        }
        throw new FormatException();
    }

    /// <summary>
    /// Пропуск значения любого типа (неизвестное поле). Без рекурсии — глубина
    /// вложенности файла не должна упираться в стек.
    /// </summary>
    public void SkipValue()
    {
        int depth = 0;
        do
        {
            char c = Peek();
            switch (c)
            {
                case '"':
                    ReadString();
                    break;
                case '{' or '[':
                    depth++;
                    _pos++;
                    break;
                case '}' or ']':
                    depth--;
                    _pos++;
                    break;
                case ',' or ':' when depth > 0:
                    _pos++;
                    break;
                case ',' or ':' or '\0':
                    throw new FormatException();
                default:
                    // Литерал: число, true, false, null
                    while (_pos < _text.Length
                        && _text[_pos] is not (' ' or '\t' or '\r' or '\n' or ',' or ':' or '{' or '}' or '[' or ']' or '"'))
                        _pos++;
                    break;
            }
        } while (depth > 0);

        if (depth < 0)
            throw new FormatException();
    }
}

internal static class JsonWriter
{
    /// <summary>Строка в кавычках с экранированием по правилам JSON.</summary>
    public static void AppendString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (char c in value)
        {
            if (c is '"' or '\\')
                sb.Append('\\').Append(c);
            else if (c < ' ')
                sb.Append("\\u00").Append(HexDigit(c >> 4)).Append(HexDigit(c & 0xF));
            else
                sb.Append(c);
        }
        sb.Append('"');
    }

    static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'a' + value - 10);
}
