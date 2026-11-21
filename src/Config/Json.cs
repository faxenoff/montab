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

    /// <summary>
    /// Десятичное число по грамматике JSON. До 15 значащих цифр результат тот же,
    /// что у double.Parse; на более длинных записях возможно расхождение в последних битах.
    /// </summary>
    public double ReadNumber()
    {
        // Мантисса копится, пока следующая цифра гарантированно помещается в ulong
        const ulong MantissaLimit = (ulong.MaxValue - 9) / 10;

        bool negative = TryConsume('-');
        ulong mantissa = 0;
        int scale = 0;

        int digits = 0;
        for (; _pos < _text.Length && _text[_pos] is >= '0' and <= '9'; _pos++, digits++)
        {
            if (mantissa <= MantissaLimit)
                mantissa = mantissa * 10 + (uint)(_text[_pos] - '0');
            else
                scale++;
        }
        if (digits == 0)
            throw new FormatException();

        if (_pos < _text.Length && _text[_pos] == '.')
        {
            _pos++;
            digits = 0;
            for (; _pos < _text.Length && _text[_pos] is >= '0' and <= '9'; _pos++, digits++)
            {
                if (mantissa <= MantissaLimit)
                {
                    mantissa = mantissa * 10 + (uint)(_text[_pos] - '0');
                    scale--;
                }
            }
            if (digits == 0)
                throw new FormatException();
        }

        if (_pos < _text.Length && _text[_pos] is 'e' or 'E')
        {
            _pos++;
            bool negativeExponent = false;
            if (_pos < _text.Length && _text[_pos] is '+' or '-')
                negativeExponent = _text[_pos++] == '-';
            int exponent = 0;
            digits = 0;
            for (; _pos < _text.Length && _text[_pos] is >= '0' and <= '9'; _pos++, digits++)
            {
                // Дальше 10^±400 double всё равно не различает — порядок не накапливаем
                if (exponent < 1000)
                    exponent = exponent * 10 + (_text[_pos] - '0');
            }
            if (digits == 0)
                throw new FormatException();
            scale += negativeExponent ? -exponent : exponent;
        }

        double power = 1;
        for (int i = Math.Min(Math.Abs(scale), 400); i > 0; i--)
            power *= 10;
        double value = scale < 0 ? mantissa / power : mantissa * power;
        return negative ? -value : value;
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

    /// <summary>Число с фиксированной точностью: до шести знаков после запятой, без хвостовых нулей.</summary>
    public static void AppendNumber(StringBuilder sb, double value)
    {
        const int Decimals = 6;
        const long Scale = 1_000_000;

        long scaled = (long)Math.Round(value * Scale);
        if (scaled < 0)
        {
            sb.Append('-');
            scaled = -scaled;
        }
        sb.Append(scaled / Scale);

        long fraction = scaled % Scale;
        if (fraction == 0)
            return;
        int width = Decimals;
        for (; fraction % 10 == 0; fraction /= 10)
            width--;
        Span<char> text = stackalloc char[Decimals];
        for (int i = width - 1; i >= 0; i--, fraction /= 10)
            text[i] = (char)('0' + fraction % 10);
        sb.Append('.').Append(text[..width]);
    }

    static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'a' + value - 10);
}
