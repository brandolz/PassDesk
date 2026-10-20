using PassDesk.Crypto;

namespace PassDesk.Import.Csv;

/// <summary>Posición de un campo dentro del texto CSV (sin copiar su contenido).</summary>
internal readonly record struct CsvField(int Start, int Length, bool HasEscapedQuotes);

internal sealed class CsvTable
{
    public List<CsvField[]> Rows { get; } = [];

    /// <summary>Número de línea (1-based) donde empieza cada fila.</summary>
    public List<int> RowLines { get; } = [];
}

/// <summary>Error de formato CSV. El mensaje es genérico y nunca incluye contenido del archivo.</summary>
public sealed class CsvFormatException(int line, string reason) : Exception(reason)
{
    public int Line { get; } = line;
}

/// <summary>
/// Analizador CSV (RFC 4180) que trabaja sobre el texto original sin crear cadenas por campo.
/// Así las contraseñas pueden pasar directamente del búfer del archivo a un
/// <see cref="ProtectedSecret"/> sin copias intermedias en el montón gestionado.
/// </summary>
internal static class CsvParser
{
    public const int MaxFieldsPerRow = 512;

    public static char DetectDelimiter(ReadOnlySpan<char> text)
    {
        int end = text.IndexOfAny('\r', '\n');
        var header = end < 0 ? text : text[..end];
        int commas = header.Count(',');
        int semicolons = header.Count(';');
        int tabs = header.Count('\t');
        if (semicolons > commas && semicolons >= tabs)
        {
            return ';';
        }

        return tabs > commas && tabs > semicolons ? '\t' : ',';
    }

    public static CsvTable Parse(ReadOnlySpan<char> text, char delimiter, int maxRows)
    {
        var table = new CsvTable();
        var fields = new List<CsvField>(16);
        int length = text.Length;
        int i = length > 0 && text[0] == '﻿' ? 1 : 0;
        int line = 1;
        int rowLine = 1;

        while (i < length)
        {
            if (text[i] == '"')
            {
                int start = i + 1;
                int j = start;
                bool escaped = false;
                while (true)
                {
                    if (j >= length)
                    {
                        throw new CsvFormatException(rowLine, "hay comillas sin cerrar.");
                    }

                    char c = text[j];
                    if (c == '"')
                    {
                        if (j + 1 < length && text[j + 1] == '"')
                        {
                            escaped = true;
                            j += 2;
                            continue;
                        }

                        break;
                    }

                    if (c == '\n')
                    {
                        line++;
                    }

                    j++;
                }

                fields.Add(new CsvField(start, j - start, escaped));
                i = j + 1;
                if (i < length && text[i] != delimiter && text[i] != '\r' && text[i] != '\n')
                {
                    throw new CsvFormatException(line, "hay texto inesperado después de unas comillas.");
                }
            }
            else
            {
                int start = i;
                while (i < length && text[i] != delimiter && text[i] != '\r' && text[i] != '\n')
                {
                    i++;
                }

                fields.Add(new CsvField(start, i - start, false));
            }

            if (fields.Count > MaxFieldsPerRow)
            {
                throw new CsvFormatException(rowLine, "una fila tiene demasiadas columnas.");
            }

            if (i >= length)
            {
                break;
            }

            char separator = text[i];
            if (separator == delimiter)
            {
                i++;
                if (i >= length)
                {
                    fields.Add(new CsvField(i, 0, false));
                    break;
                }

                continue;
            }

            // Fin de línea: \r\n, \n o \r.
            i++;
            if (separator == '\r' && i < length && text[i] == '\n')
            {
                i++;
            }

            table.Rows.Add([.. fields]);
            table.RowLines.Add(rowLine);
            fields.Clear();
            line++;
            rowLine = line;
            if (table.Rows.Count > maxRows)
            {
                throw new CsvFormatException(rowLine, "el archivo tiene demasiadas filas.");
            }
        }

        if (fields.Count > 0)
        {
            table.Rows.Add([.. fields]);
            table.RowLines.Add(rowLine);
        }

        return table;
    }

    public static bool IsBlankRow(ReadOnlySpan<char> text, CsvField[] row)
    {
        foreach (var field in row)
        {
            if (!text.Slice(field.Start, field.Length).IsWhiteSpace())
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Texto de un campo no sensible (título, URL, usuario, cabeceras).</summary>
    public static string GetString(ReadOnlySpan<char> text, CsvField field)
    {
        var span = text.Slice(field.Start, field.Length);
        return field.HasEscapedQuotes
            ? span.ToString().Replace("\"\"", "\"", StringComparison.Ordinal)
            : span.ToString();
    }

    /// <summary>Convierte un campo sensible directamente en un secreto protegido.</summary>
    public static ProtectedSecret GetSecret(ReadOnlySpan<char> text, CsvField field)
    {
        var span = text.Slice(field.Start, field.Length);
        if (!field.HasEscapedQuotes)
        {
            return ProtectedSecret.FromChars(span);
        }

        using var unescaped = new SensitiveChars(span.Length);
        int written = 0;
        var destination = unescaped.Span;
        for (int k = 0; k < span.Length; k++)
        {
            destination[written++] = span[k];
            if (span[k] == '"' && k + 1 < span.Length && span[k + 1] == '"')
            {
                k++;
            }
        }

        return ProtectedSecret.FromChars(destination[..written]);
    }
}
