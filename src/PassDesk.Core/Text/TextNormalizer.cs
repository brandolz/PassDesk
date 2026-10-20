using System.Globalization;
using System.Text;

namespace PassDesk.Core.Text;

/// <summary>Normaliza texto para búsquedas: minúsculas y sin tildes ni diacríticos.</summary>
public static class TextNormalizer
{
    public static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Divide una consulta en términos normalizados no vacíos.</summary>
    public static string[] Terms(string? query) =>
        Fold(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
