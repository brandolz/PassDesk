using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PassDesk.Crypto;

namespace PassDesk.Core.Security;

public enum TotpAlgorithm
{
    Sha1,
    Sha256,
    Sha512,
}

/// <summary>
/// Códigos de verificación TOTP (RFC 6238) a partir de la URI <c>otpauth://totp/...</c>
/// que Apple incluye en la columna OTPAuth de la exportación. La semilla se mantiene cifrada en
/// memoria y solo se descifra durante el cálculo del código.
/// </summary>
public sealed class TotpGenerator : IDisposable
{
    private static readonly int[] Powers = [1, 10, 100, 1_000, 10_000, 100_000, 1_000_000, 10_000_000, 100_000_000];
    private readonly ProtectedSecret _key;

    private TotpGenerator(ProtectedSecret key, int digits, int period, TotpAlgorithm algorithm)
    {
        _key = key;
        Digits = digits;
        Period = period;
        Algorithm = algorithm;
    }

    public int Digits { get; }

    public int Period { get; }

    public TotpAlgorithm Algorithm { get; }

    /// <summary>Crea el generador o devuelve null si la URI no es un TOTP válido.</summary>
    public static TotpGenerator? TryCreate(ProtectedSecret otpAuthUri)
    {
        ArgumentNullException.ThrowIfNull(otpAuthUri);
        if (otpAuthUri.IsEmpty)
        {
            return null;
        }

        using var uri = otpAuthUri.Unprotect();
        return TryParse(uri.Span);
    }

    /// <summary>Crea un generador directamente a partir de una clave binaria (pruebas y RFC 6238).</summary>
    internal static TotpGenerator FromRawKey(ReadOnlySpan<byte> key, int digits, int period, TotpAlgorithm algorithm) =>
        new(ProtectedSecret.FromBytes(key), digits, period, algorithm);

    internal static TotpGenerator? TryParse(ReadOnlySpan<byte> uri)
    {
        ReadOnlySpan<byte> prefix = "otpauth://totp/"u8;
        if (uri.Length <= prefix.Length || !Ascii.EqualsIgnoreCase(uri[..prefix.Length], prefix))
        {
            return null;
        }

        int question = uri.IndexOf((byte)'?');
        if (question < 0)
        {
            return null;
        }

        int digits = 6;
        int period = 30;
        var algorithm = TotpAlgorithm.Sha1;
        ProtectedSecret? key = null;
        bool valid = true;
        var query = uri[(question + 1)..];

        while (!query.IsEmpty && valid)
        {
            int amp = query.IndexOf((byte)'&');
            var pair = amp < 0 ? query : query[..amp];
            query = amp < 0 ? [] : query[(amp + 1)..];

            int eq = pair.IndexOf((byte)'=');
            if (eq <= 0)
            {
                continue;
            }

            var name = pair[..eq];
            var value = pair[(eq + 1)..];
            if (Ascii.EqualsIgnoreCase(name, "secret"u8))
            {
                key?.Dispose();
                key = DecodeSecret(value);
                valid = key is not null;
            }
            else if (Ascii.EqualsIgnoreCase(name, "digits"u8))
            {
                valid = TryParseInt(value, out digits) && digits is >= 6 and <= 8;
            }
            else if (Ascii.EqualsIgnoreCase(name, "period"u8))
            {
                valid = TryParseInt(value, out period) && period is >= 1 and <= 300;
            }
            else if (Ascii.EqualsIgnoreCase(name, "algorithm"u8))
            {
                if (Ascii.EqualsIgnoreCase(value, "SHA1"u8))
                {
                    algorithm = TotpAlgorithm.Sha1;
                }
                else if (Ascii.EqualsIgnoreCase(value, "SHA256"u8))
                {
                    algorithm = TotpAlgorithm.Sha256;
                }
                else if (Ascii.EqualsIgnoreCase(value, "SHA512"u8))
                {
                    algorithm = TotpAlgorithm.Sha512;
                }
                else
                {
                    valid = false;
                }
            }
        }

        if (!valid || key is null || key.IsEmpty)
        {
            key?.Dispose();
            return null;
        }

        return new TotpGenerator(key, digits, period, algorithm);
    }

    public string ComputeCode(DateTimeOffset now)
    {
        long counter = now.ToUnixTimeSeconds() / Period;
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);
        Span<byte> hash = stackalloc byte[64];
        try
        {
            int length;
            using (var key = _key.Unprotect())
            {
                length = Algorithm switch
                {
                    TotpAlgorithm.Sha256 => HMACSHA256.HashData(key.Span, message, hash),
                    TotpAlgorithm.Sha512 => HMACSHA512.HashData(key.Span, message, hash),
                    _ => HMACSHA1.HashData(key.Span, message, hash),
                };
            }

            int offset = hash[length - 1] & 0x0F;
            int binary = ((hash[offset] & 0x7F) << 24)
                | (hash[offset + 1] << 16)
                | (hash[offset + 2] << 8)
                | hash[offset + 3];
            int code = binary % Powers[Digits];
            return code.ToString("D" + Digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public int SecondsRemaining(DateTimeOffset now) => Period - (int)(now.ToUnixTimeSeconds() % Period);

    public void Dispose() => _key.Dispose();

    public override string ToString() => "TotpGenerator([redacted])";

    private static bool TryParseInt(ReadOnlySpan<byte> value, out int result) =>
        Utf8Parser.TryParse(value, out result, out int consumed) && consumed == value.Length;

    private static ProtectedSecret? DecodeSecret(ReadOnlySpan<byte> encoded)
    {
        using var unescaped = new SensitiveBuffer(encoded.Length);
        int length = PercentDecode(encoded, unescaped.Span);
        if (length <= 0)
        {
            return null;
        }

        using var raw = new SensitiveBuffer((length * 5 / 8) + 1);
        int written = Base32Decode(unescaped.Span[..length], raw.Span);
        return written <= 0 ? null : ProtectedSecret.FromBytes(raw.Span[..written]);
    }

    private static int PercentDecode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int count = 0;
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == (byte)'%' && i + 2 < input.Length
                && Utf8Parser.TryParse(input.Slice(i + 1, 2), out byte value, out int consumed, 'X') && consumed == 2)
            {
                output[count++] = value;
                i += 2;
            }
            else
            {
                output[count++] = input[i];
            }
        }

        return count;
    }

    /// <summary>Decodifica Base32 (RFC 4648) ignorando relleno, espacios y guiones. Devuelve -1 si no es válido.</summary>
    internal static int Base32Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int buffer = 0;
        int bits = 0;
        int count = 0;
        foreach (byte c in input)
        {
            int value;
            if (c is (byte)'=' or (byte)' ' or (byte)'-')
            {
                continue;
            }
            else if (c is >= (byte)'A' and <= (byte)'Z')
            {
                value = c - 'A';
            }
            else if (c is >= (byte)'a' and <= (byte)'z')
            {
                value = c - 'a';
            }
            else if (c is >= (byte)'2' and <= (byte)'7')
            {
                value = c - '2' + 26;
            }
            else
            {
                return -1;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output[count++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }

        return count;
    }
}
