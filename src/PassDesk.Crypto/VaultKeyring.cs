using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PassDesk.Crypto;

/// <summary>
/// Cabecera de claves del sobre cifrado. Contiene los parámetros de derivación (PBKDF2)
/// y la clave de datos envuelta con la clave derivada de la contraseña maestra.
/// </summary>
/// <remarks>
/// Formato (little-endian):
/// <code>
/// 0   4  "PDE1"           magia
/// 4   1  versión (1)
/// 5   1  KDF (1 = PBKDF2-HMAC-SHA256, 2 = Argon2id con 64 MiB)
/// 6   4  coste (iteraciones de PBKDF2 o pasadas de Argon2id)
/// 10  16 sal
/// 26  12 nonce de envoltura      (AAD de la envoltura = bytes 0..25)
/// 38  32 clave de datos envuelta
/// 70  16 etiqueta GCM de envoltura
/// </code>
/// El KDF y su coste forman parte de los datos autenticados: no se pueden rebajar sin que falle el descifrado.
/// </remarks>
public sealed class KeyHeader
{
    public const int Length = 86;
    internal const int WrapAadLength = 26;
    internal static ReadOnlySpan<byte> Magic => "PDE1"u8;
    internal const byte FormatVersion = 1;

    private readonly byte[] _bytes;

    private KeyHeader(byte[] bytes)
    {
        _bytes = bytes;
        Iterations = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(6, 4));
        Kdf = new KdfSettings((KdfAlgorithm)bytes[5], Iterations);
    }

    /// <summary>Coste del KDF (iteraciones de PBKDF2 o pasadas de Argon2id).</summary>
    public int Iterations { get; }

    public KdfSettings Kdf { get; }

    public ReadOnlySpan<byte> Bytes => _bytes;

    internal ReadOnlySpan<byte> Salt => _bytes.AsSpan(10, 16);

    internal ReadOnlySpan<byte> WrapNonce => _bytes.AsSpan(26, 12);

    internal ReadOnlySpan<byte> WrappedKey => _bytes.AsSpan(38, 32);

    internal ReadOnlySpan<byte> WrapTag => _bytes.AsSpan(70, 16);

    internal ReadOnlySpan<byte> WrapAad => _bytes.AsSpan(0, WrapAadLength);

    public static KeyHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Length || !data[..4].SequenceEqual(Magic))
        {
            throw new VaultFormatException("El archivo no tiene el formato de bóveda de PassDesk.");
        }

        if (data[4] != FormatVersion)
        {
            throw new VaultFormatException("Versión de bóveda no compatible.");
        }

        if (data[5] is not ((byte)KdfAlgorithm.Pbkdf2Sha256 or (byte)KdfAlgorithm.Argon2id64MiB))
        {
            throw new VaultFormatException("Función de derivación de claves no compatible.");
        }

        var kdf = new KdfSettings((KdfAlgorithm)data[5], BinaryPrimitives.ReadInt32LittleEndian(data.Slice(6, 4)));
        if (!kdf.IsValid)
        {
            throw new VaultFormatException("Parámetros de derivación de claves fuera de rango.");
        }

        return new KeyHeader(data[..Length].ToArray());
    }

    internal static KeyHeader FromBytes(byte[] bytes) => new(bytes);

    public bool SameAs(KeyHeader other) => _bytes.AsSpan().SequenceEqual(other._bytes);
}

/// <summary>
/// Llavero de la bóveda: cifrado por sobre (envelope encryption) con primitivas estándar de .NET/CNG.
/// <list type="bullet">
/// <item>Clave de datos (DEK) aleatoria de 256 bits que cifra el contenido con AES-256-GCM.</item>
/// <item>Clave de cifrado de claves (KEK) derivada de la contraseña maestra con Argon2id (64 MiB, 3 pasadas,
/// mediante libsodium) y sal aleatoria de 128 bits. Las bóvedas antiguas con PBKDF2-HMAC-SHA256 (600.000
/// iteraciones) se siguen abriendo y se mejoran a Argon2id al desbloquearlas.</item>
/// <item>La DEK se guarda envuelta con la KEK (AES-256-GCM). En memoria se mantiene protegida con
/// <c>CryptProtectMemory</c> y solo se descifra durante cada operación.</item>
/// </list>
/// No se implementa ningún algoritmo criptográfico propio.
/// </summary>
/// <remarks>
/// Formato del sobre completo: <see cref="KeyHeader"/> (86 bytes) seguido de
/// <code>
/// 86  12 nonce de datos
/// 98  16 etiqueta GCM de datos
/// 114 4  longitud del texto cifrado
/// 118 N  texto cifrado            (AAD de los datos = bytes 0..85)
/// </code>
/// </remarks>
public sealed class VaultKeyring : IDisposable
{
    public const int DefaultIterations = 600_000;
    public const int MinIterations = 10_000;
    public const int MaxIterations = 10_000_000;
    public const int MaxPayloadLength = 64 * 1024 * 1024;

    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int DataNonceOffset = KeyHeader.Length;
    private const int DataTagOffset = DataNonceOffset + NonceSize;
    private const int DataLengthOffset = DataTagOffset + TagSize;
    private const int DataOffset = DataLengthOffset + sizeof(int);

    private readonly ProtectedSecret _dataKey;

    private VaultKeyring(KeyHeader header, ProtectedSecret dataKey)
    {
        Header = header;
        _dataKey = dataKey;
    }

    public KeyHeader Header { get; }

    /// <summary>Crea un llavero nuevo (Argon2id) con una clave de datos aleatoria protegida por la contraseña.</summary>
    public static VaultKeyring Create(ReadOnlySpan<char> password) => Create(password, KdfSettings.Default);

    /// <summary>Crea un llavero nuevo con PBKDF2-HMAC-SHA256 (compatibilidad y pruebas rápidas).</summary>
    public static VaultKeyring Create(ReadOnlySpan<char> password, int iterations)
    {
        if (iterations < MinIterations || iterations > MaxIterations)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }

        return Create(password, KdfSettings.Pbkdf2(iterations));
    }

    /// <summary>Crea un llavero nuevo con los parámetros de derivación indicados.</summary>
    public static VaultKeyring Create(ReadOnlySpan<char> password, KdfSettings kdf)
    {
        if (password.IsEmpty)
        {
            throw new ArgumentException("La contraseña no puede estar vacía.", nameof(password));
        }

        using var dataKey = new SensitiveBuffer(KeySize);
        RandomNumberGenerator.Fill(dataKey.Span);
        return Wrap(dataKey.Span, password, kdf);
    }

    /// <summary>
    /// Vuelve a proteger la MISMA clave de datos con otros parámetros de derivación (sal y nonce nuevos). Se usa
    /// para pasar las bóvedas antiguas a Argon2id. Antes de devolverlo comprueba que el nuevo llavero se abre con
    /// la contraseña y recupera exactamente la misma clave, para no dejar nunca una bóveda inaccesible.
    /// </summary>
    public VaultKeyring Rewrap(ReadOnlySpan<char> password, KdfSettings kdf)
    {
        if (password.IsEmpty)
        {
            throw new ArgumentException("La contraseña no puede estar vacía.", nameof(password));
        }

        using var key = _dataKey.Unprotect();
        var rewrapped = Wrap(key.Span, password, kdf);
        try
        {
            using var check = Unlock(rewrapped.Header, password);
            using var checkKey = check._dataKey.Unprotect();
            if (!CryptographicOperations.FixedTimeEquals(checkKey.Span, key.Span))
            {
                throw new CryptographicException("La comprobación del nuevo llavero ha fallado.");
            }

            return rewrapped;
        }
        catch
        {
            rewrapped.Dispose();
            throw;
        }
    }

    private static VaultKeyring Wrap(ReadOnlySpan<byte> dataKey, ReadOnlySpan<char> password, KdfSettings kdf)
    {
        ArgumentNullException.ThrowIfNull(kdf);
        if (!kdf.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(kdf));
        }

        var headerBytes = new byte[KeyHeader.Length];
        KeyHeader.Magic.CopyTo(headerBytes);
        headerBytes[4] = KeyHeader.FormatVersion;
        headerBytes[5] = (byte)kdf.Algorithm;
        BinaryPrimitives.WriteInt32LittleEndian(headerBytes.AsSpan(6, 4), kdf.Cost);
        RandomNumberGenerator.Fill(headerBytes.AsSpan(10, SaltSize));
        RandomNumberGenerator.Fill(headerBytes.AsSpan(26, NonceSize));

        using (var kek = DeriveKek(password, headerBytes.AsSpan(10, SaltSize), kdf))
        using (var aes = new AesGcm(kek.Span, TagSize))
        {
            aes.Encrypt(
                nonce: headerBytes.AsSpan(26, NonceSize),
                plaintext: dataKey,
                ciphertext: headerBytes.AsSpan(38, KeySize),
                tag: headerBytes.AsSpan(70, TagSize),
                associatedData: headerBytes.AsSpan(0, KeyHeader.WrapAadLength));
        }

        return new VaultKeyring(KeyHeader.FromBytes(headerBytes), ProtectedSecret.FromBytes(dataKey));
    }

    /// <summary>Recupera la clave de datos con la contraseña maestra.</summary>
    /// <exception cref="InvalidMasterPasswordException">Contraseña incorrecta o cabecera manipulada.</exception>
    public static VaultKeyring Unlock(KeyHeader header, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (password.IsEmpty)
        {
            throw new InvalidMasterPasswordException();
        }

        using var dataKey = new SensitiveBuffer(KeySize);
        using (var kek = DeriveKek(password, header.Salt, header.Kdf))
        {
            try
            {
                using var aes = new AesGcm(kek.Span, TagSize);
                aes.Decrypt(header.WrapNonce, header.WrappedKey, header.WrapTag, dataKey.Span, header.WrapAad);
            }
            catch (CryptographicException)
            {
                throw new InvalidMasterPasswordException();
            }
        }

        return new VaultKeyring(header, ProtectedSecret.FromBytes(dataKey.Span));
    }

    /// <summary>Lee y valida solo la cabecera de claves de un sobre.</summary>
    public static KeyHeader ReadHeader(ReadOnlySpan<byte> envelope) => KeyHeader.Parse(envelope);

    /// <summary>Cifra el contenido con un nonce aleatorio nuevo y devuelve el sobre completo.</summary>
    public byte[] Seal(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentException("El contenido de la bóveda es demasiado grande.", nameof(payload));
        }

        var envelope = new byte[DataOffset + payload.Length];
        Header.Bytes.CopyTo(envelope);
        RandomNumberGenerator.Fill(envelope.AsSpan(DataNonceOffset, NonceSize));
        BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(DataLengthOffset, sizeof(int)), payload.Length);

        using var key = _dataKey.Unprotect();
        using var aes = new AesGcm(key.Span, TagSize);
        aes.Encrypt(
            nonce: envelope.AsSpan(DataNonceOffset, NonceSize),
            plaintext: payload,
            ciphertext: envelope.AsSpan(DataOffset),
            tag: envelope.AsSpan(DataTagOffset, TagSize),
            associatedData: envelope.AsSpan(0, KeyHeader.Length));
        return envelope;
    }

    /// <summary>Descifra y verifica un sobre. El resultado debe liberarse (se pone a cero).</summary>
    /// <exception cref="VaultFormatException">Formato no válido, otra clave o integridad comprometida.</exception>
    public SensitiveBuffer Open(ReadOnlySpan<byte> envelope)
    {
        var header = KeyHeader.Parse(envelope);
        if (!header.SameAs(Header))
        {
            throw new VaultFormatException("La bóveda se cifró con otra clave.");
        }

        if (envelope.Length < DataOffset)
        {
            throw new VaultFormatException("La bóveda está incompleta.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(DataLengthOffset, sizeof(int)));
        if (length < 0 || length > MaxPayloadLength || envelope.Length != DataOffset + length)
        {
            throw new VaultFormatException("La bóveda está dañada o incompleta.");
        }

        var plaintext = new SensitiveBuffer(length);
        try
        {
            using var key = _dataKey.Unprotect();
            using var aes = new AesGcm(key.Span, TagSize);
            aes.Decrypt(
                nonce: envelope.Slice(DataNonceOffset, NonceSize),
                ciphertext: envelope.Slice(DataOffset, length),
                tag: envelope.Slice(DataTagOffset, TagSize),
                plaintext: plaintext.Span,
                associatedData: envelope[..KeyHeader.Length]);
            return plaintext;
        }
        catch (CryptographicException)
        {
            plaintext.Dispose();
            throw new VaultFormatException("La verificación de integridad de la bóveda ha fallado.");
        }
    }

    public void Dispose() => _dataKey.Dispose();

    private static SensitiveBuffer DeriveKek(ReadOnlySpan<char> password, ReadOnlySpan<byte> salt, KdfSettings kdf)
    {
        var kek = new SensitiveBuffer(KeySize);
        try
        {
            kdf.Derive(password, salt, kek.Span);
            return kek;
        }
        catch
        {
            kek.Dispose();
            throw;
        }
    }
}
