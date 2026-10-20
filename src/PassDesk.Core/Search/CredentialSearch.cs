using System.Globalization;
using PassDesk.Core.Model;
using PassDesk.Core.Text;

namespace PassDesk.Core.Search;

public enum SortMode
{
    NameAscending,
    NameDescending,
    ModifiedNewest,
    ModifiedOldest,
}

/// <summary>
/// Búsqueda instantánea por servicio, dominio, URL y usuario/correo, sin distinguir
/// mayúsculas ni tildes. Todos los términos deben aparecer (búsqueda AND).
/// Nunca busca dentro de contraseñas ni notas.
/// </summary>
public static class CredentialSearch
{
    public static List<CredentialEntry> Apply(
        IEnumerable<CredentialEntry> entries,
        string? query,
        Category? category,
        SortMode sort)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string[] terms = TextNormalizer.Terms(query);
        var matches = new List<(CredentialEntry Entry, int Score)>();

        foreach (var entry in entries)
        {
            if (category is { } wanted && entry.Category != wanted)
            {
                continue;
            }

            if (terms.Length == 0)
            {
                matches.Add((entry, 0));
                continue;
            }

            if (Score(entry, terms) is { } score)
            {
                matches.Add((entry, score));
            }
        }

        var nameComparer = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
        matches.Sort((a, b) =>
        {
            int byScore = a.Score.CompareTo(b.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            int result = sort switch
            {
                SortMode.NameDescending => nameComparer.Compare(b.Entry.Title, a.Entry.Title),
                SortMode.ModifiedNewest => b.Entry.ModifiedUtc.CompareTo(a.Entry.ModifiedUtc),
                SortMode.ModifiedOldest => a.Entry.ModifiedUtc.CompareTo(b.Entry.ModifiedUtc),
                _ => nameComparer.Compare(a.Entry.Title, b.Entry.Title),
            };

            return result != 0 ? result : nameComparer.Compare(a.Entry.Username, b.Entry.Username);
        });

        return matches.ConvertAll(m => m.Entry);
    }

    /// <summary>
    /// Devuelve null si no coincide. Menor puntuación = mejor resultado:
    /// 0 título que empieza por el término, 1 dominio, 2 usuario, 3 coincidencia parcial.
    /// </summary>
    internal static int? Score(CredentialEntry entry, string[] terms)
    {
        var fields = entry.Search;
        foreach (string term in terms)
        {
            if (!fields.Haystack.Contains(term, StringComparison.Ordinal))
            {
                return null;
            }
        }

        string first = terms[0];
        if (fields.Title.StartsWith(first, StringComparison.Ordinal))
        {
            return 0;
        }

        if (fields.Domain.StartsWith(first, StringComparison.Ordinal)
            || fields.Domain.Contains("." + first, StringComparison.Ordinal))
        {
            return 1;
        }

        if (fields.Username.StartsWith(first, StringComparison.Ordinal))
        {
            return 2;
        }

        return 3;
    }
}
