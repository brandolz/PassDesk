using System.Security.Cryptography;
using PassDesk.Crypto;

namespace PassDesk.Core.Security;

public sealed record PasswordGeneratorOptions(
    int Length = 20,
    bool Uppercase = true,
    bool Digits = true,
    bool Symbols = true,
    bool ExcludeAmbiguous = false);

/// <summary>
/// Generador de contraseñas con <see cref="RandomNumberGenerator"/> (CSPRNG del sistema).
/// Usa <see cref="RandomNumberGenerator.GetInt32(int)"/>, que no tiene sesgo de módulo,
/// y garantiza al menos un carácter de cada grupo seleccionado. Las minúsculas siempre se incluyen.
/// </summary>
public static class PasswordGenerator
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string DigitChars = "0123456789";
    private const string SymbolChars = "!@#$%^&*()-_=+[]{};:,.?/~";
    private const string Ambiguous = "Il1O0o|`'\"";

    public static string Generate(PasswordGeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Length < MinLength || options.Length > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"La longitud debe estar entre {MinLength} y {MaxLength}.");
        }

        string[] groups = GetGroups(options);
        string pool = string.Concat(groups);
        using var chars = new SensitiveChars(options.Length);
        var span = chars.Span;

        // Un carácter garantizado de cada grupo y el resto del conjunto completo.
        for (int i = 0; i < groups.Length; i++)
        {
            span[i] = groups[i][RandomNumberGenerator.GetInt32(groups[i].Length)];
        }

        for (int i = groups.Length; i < span.Length; i++)
        {
            span[i] = pool[RandomNumberGenerator.GetInt32(pool.Length)];
        }

        // Barajado de Fisher-Yates con el CSPRNG para no dejar los obligatorios al principio.
        for (int i = span.Length - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (span[i], span[j]) = (span[j], span[i]);
        }

        return new string(span);
    }

    /// <summary>Entropía teórica en bits de una contraseña generada con estas opciones.</summary>
    public static double EstimateEntropyBits(PasswordGeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        int poolSize = GetGroups(options).Sum(g => g.Length);
        return options.Length * Math.Log2(poolSize);
    }

    internal static string[] GetGroups(PasswordGeneratorOptions options)
    {
        var groups = new List<string> { Filter(Lower, options.ExcludeAmbiguous) };
        if (options.Uppercase)
        {
            groups.Add(Filter(Upper, options.ExcludeAmbiguous));
        }

        if (options.Digits)
        {
            groups.Add(Filter(DigitChars, options.ExcludeAmbiguous));
        }

        if (options.Symbols)
        {
            groups.Add(Filter(SymbolChars, options.ExcludeAmbiguous));
        }

        return [.. groups];
    }

    private static string Filter(string set, bool excludeAmbiguous) =>
        excludeAmbiguous ? new string(set.Where(c => !Ambiguous.Contains(c)).ToArray()) : set;
}
