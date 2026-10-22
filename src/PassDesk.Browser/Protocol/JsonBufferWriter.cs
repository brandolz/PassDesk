using System.Text;
using System.Text.Unicode;

namespace PassDesk.Browser.Protocol;

/// <summary>
/// Escribe JSON directamente sobre un <see cref="PinnedBuffer"/>. Las respuestas con contraseñas no pasan por
/// <c>Utf8JsonWriter</c> porque este escapa el texto en búferes compartidos (<c>ArrayPool</c>) que no se ponen a cero;
/// aquí los bytes de la contraseña se copian una sola vez, ya escapados, al búfer que se borra al terminar.
/// </summary>
internal sealed class JsonBufferWriter(PinnedBuffer buffer)
{
    private const string Hex = "0123456789abcdef";
    private bool _needsComma;

    public void BeginObject()
    {
        buffer.Append((byte)'{');
        _needsComma = false;
    }

    public void EndObject()
    {
        buffer.Append((byte)'}');
        _needsComma = true;
    }

    public void BeginArray(string name)
    {
        Name(name);
        buffer.Append((byte)'[');
        _needsComma = false;
    }

    public void EndArray()
    {
        buffer.Append((byte)']');
        _needsComma = true;
    }

    /// <summary>Elemento de matriz que es un objeto.</summary>
    public void BeginElementObject()
    {
        if (_needsComma)
        {
            buffer.Append((byte)',');
        }

        buffer.Append((byte)'{');
        _needsComma = false;
    }

    public void Number(string name, long value)
    {
        Name(name);
        buffer.Append(Encoding.ASCII.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public void Bool(string name, bool value)
    {
        Name(name);
        buffer.Append(Encoding.ASCII.GetBytes(value ? "true" : "false"));
    }

    public void String(string name, string value)
    {
        Name(name);
        WriteString(value);
    }

    /// <summary>Texto ya codificado en UTF-8 (una contraseña): se escapa byte a byte, sin crear cadenas.</summary>
    public void Utf8String(string name, ReadOnlySpan<byte> utf8)
    {
        if (!Utf8.IsValid(utf8))
        {
            throw new InvalidOperationException("Texto UTF-8 no válido.");
        }

        Name(name);
        buffer.Append((byte)'"');
        foreach (byte b in utf8)
        {
            AppendEscaped(b);
        }

        buffer.Append((byte)'"');
    }

    private void Name(string name)
    {
        if (_needsComma)
        {
            buffer.Append((byte)',');
        }

        WriteString(name);
        buffer.Append((byte)':');
        _needsComma = true;
    }

    private void WriteString(string value)
    {
        buffer.Append((byte)'"');
        Span<byte> scratch = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value < 0x80)
            {
                AppendEscaped((byte)rune.Value);
            }
            else
            {
                int written = rune.EncodeToUtf8(scratch);
                buffer.Append(scratch[..written]);
            }
        }

        buffer.Append((byte)'"');
    }

    private void AppendEscaped(byte b)
    {
        switch (b)
        {
            case (byte)'"':
                buffer.Append("\\\""u8);
                break;
            case (byte)'\\':
                buffer.Append("\\\\"u8);
                break;
            case (byte)'\n':
                buffer.Append("\\n"u8);
                break;
            case (byte)'\r':
                buffer.Append("\\r"u8);
                break;
            case (byte)'\t':
                buffer.Append("\\t"u8);
                break;
            case < 0x20:
                buffer.Append("\\u00"u8);
                buffer.Append((byte)Hex[b >> 4]);
                buffer.Append((byte)Hex[b & 0xF]);
                break;
            default:
                buffer.Append(b);
                break;
        }
    }
}
