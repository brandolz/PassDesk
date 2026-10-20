using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace PassDesk.Crypto;

/// <summary>Función de derivación de la clave a partir de la contraseña maestra (byte 5 de la cabecera).</summary>
public enum KdfAlgorithm : byte
{
    /// <summary>PBKDF2-HMAC-SHA256 (bóvedas creadas antes de la versión con Argon2id).</summary>
    Pbkdf2Sha256 = 1,

    /// <summary>Argon2id (RFC 9106) con 64 MiB de memoria y 1 hilo; el coste indica el número de pasadas.</summary>
    Argon2id64MiB = 2,
}

/// <summary>
/// Parámetros de derivación de la clave. Argon2id es «memory-hard»: cada intento necesita 64 MiB, lo que
/// encarece mucho los ataques con GPU contra una copia robada. Se calcula con libsodium (mediante NSec), no
/// con código propio.
/// </summary>
public sealed record KdfSettings(KdfAlgorithm Algorithm, int Cost)
{
    public const int Argon2MemoryKiB = 64 * 1024;
    public const int Argon2DefaultPasses = 3;
    public const int Argon2MinPasses = 2;
    public const int Argon2MaxPasses = 10;

    /// <summary>Parámetros para bóvedas nuevas y para mejorar las antiguas al desbloquearlas.</summary>
    public static KdfSettings Default { get; } = Argon2id();

    public static KdfSettings Pbkdf2(int iterations) => new(KdfAlgorithm.Pbkdf2Sha256, iterations);

    public static KdfSettings Argon2id(int passes = Argon2DefaultPasses) => new(KdfAlgorithm.Argon2id64MiB, passes);

    public bool IsValid => Algorithm switch
    {
        KdfAlgorithm.Pbkdf2Sha256 => Cost is >= VaultKeyring.MinIterations and <= VaultKeyring.MaxIterations,
        KdfAlgorithm.Argon2id64MiB => Cost is >= Argon2MinPasses and <= Argon2MaxPasses,
        _ => false,
    };

    /// <summary>
    /// Indica si estos parámetros protegen menos que <paramref name="target"/>: PBKDF2 frente a Argon2id, o
    /// el mismo algoritmo con menos coste. Nunca se considera más débil pasar de Argon2id a PBKDF2.
    /// </summary>
    public bool IsWeakerThan(KdfSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Algorithm == target.Algorithm)
        {
            return Cost < target.Cost;
        }

        return Algorithm == KdfAlgorithm.Pbkdf2Sha256 && target.Algorithm == KdfAlgorithm.Argon2id64MiB;
    }

    /// <summary>Deriva la clave en <paramref name="output"/> (memoria fijada del llamador).</summary>
    internal void Derive(ReadOnlySpan<char> password, ReadOnlySpan<byte> salt, Span<byte> output)
    {
        if (!IsValid)
        {
            throw new CryptographicException("Parámetros de derivación de claves no válidos.");
        }

        switch (Algorithm)
        {
            case KdfAlgorithm.Pbkdf2Sha256:
                Rfc2898DeriveBytes.Pbkdf2(password, salt, output, Cost, HashAlgorithmName.SHA256);
                break;

            case KdfAlgorithm.Argon2id64MiB:
                try
                {
                    var argon2 = PasswordBasedKeyDerivationAlgorithm.Argon2id(new Argon2Parameters
                    {
                        DegreeOfParallelism = 1,
                        MemorySize = Argon2MemoryKiB,
                        NumberOfPasses = Cost,
                    });
                    using var utf8 = new SensitiveBuffer(Encoding.UTF8.GetMaxByteCount(password.Length));
                    int length = Encoding.UTF8.GetBytes(password, utf8.Span);
                    argon2.DeriveBytes(utf8.Span[..length], salt, output);
                }
                catch (Exception ex) when (ex is PlatformNotSupportedException or TypeInitializationException or DllNotFoundException
                    or BadImageFormatException or EntryPointNotFoundException)
                {
                    // libsodium no se pudo cargar (antivirus, AppLocker, ruta demasiado larga…).
                    throw new CryptoUnavailableException(ex);
                }

                break;

            default:
                throw new CryptographicException("Función de derivación de claves no compatible.");
        }
    }
}
