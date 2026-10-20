using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PassDesk.Crypto;

/// <summary>
/// Cifrado de documentos con AES-256-GCM y una clave aleatoria propia por documento.
/// La clave del documento se guarda dentro de la bóveda (protegida por la contraseña maestra), así que
/// el archivo cifrado por sí solo no sirve de nada. El identificador del documento se autentica como
/// datos asociados: un archivo cifrado no puede hacerse pasar por otro documento.
/// </summary>
/// <remarks>
/// Formato: "PDD1" | versión (1) | nonce (12) | etiqueta (16) | longitud (int32) | texto cifrado.
/// AAD = magia + versión + identificador del documento (16 bytes).
/// </remarks>
public static class DocumentCipher
{
    public const int KeySize = 32;
    public const int MaxDocumentBytes = 50 * 1024 * 1024;

    private const byte Version = 1;
    private const int NonceOffset = 5;
    private const int TagOffset = NonceOffset + 12;
    private const int LengthOffset = TagOffset + 16;
    private const int HeaderLength = LengthOffset + sizeof(int);

    private static ReadOnlySpan<byte> Magic => "PDD1"u8;

    /// <summary>Genera una clave aleatoria de 256 bits para un documento nuevo.</summary>
    public static ProtectedSecret NewKey()
    {
        Span<byte> key = stackalloc byte[KeySize];
        RandomNumberGenerator.Fill(key);
        try
        {
            return ProtectedSecret.FromBytes(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static byte[] Seal(ProtectedSecret key, Guid documentId, ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (plaintext.Length > MaxDocumentBytes)
        {
            throw new ArgumentException("El documento supera el tamaño máximo admitido.", nameof(plaintext));
        }

        var blob = new byte[HeaderLength + plaintext.Length];
        Magic.CopyTo(blob);
        blob[4] = Version;
        RandomNumberGenerator.Fill(blob.AsSpan(NonceOffset, 12));
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(LengthOffset, sizeof(int)), plaintext.Length);

        Span<byte> aad = stackalloc byte[NonceOffset + 16];
        BuildAad(blob, documentId, aad);
        using var keyBytes = key.Unprotect();
        if (keyBytes.Length != KeySize)
        {
            throw new CryptographicException("Clave de documento no válida.");
        }

        using var aes = new AesGcm(keyBytes.Span, 16);
        aes.Encrypt(
            nonce: blob.AsSpan(NonceOffset, 12),
            plaintext: plaintext,
            ciphertext: blob.AsSpan(HeaderLength),
            tag: blob.AsSpan(TagOffset, 16),
            associatedData: aad);
        return blob;
    }

    /// <summary>Descifra y verifica un documento. El resultado debe liberarse (se pone a cero).</summary>
    /// <exception cref="VaultFormatException">Formato no válido, clave incorrecta o documento manipulado.</exception>
    public static SensitiveBuffer Open(ProtectedSecret key, Guid documentId, ReadOnlySpan<byte> blob)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (blob.Length < HeaderLength || !blob[..4].SequenceEqual(Magic) || blob[4] != Version)
        {
            throw new VaultFormatException("El documento cifrado no tiene un formato válido.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(LengthOffset, sizeof(int)));
        if (length < 0 || length > MaxDocumentBytes || blob.Length != HeaderLength + length)
        {
            throw new VaultFormatException("El documento cifrado está dañado o incompleto.");
        }

        Span<byte> aad = stackalloc byte[NonceOffset + 16];
        BuildAad(blob, documentId, aad);
        var plaintext = new SensitiveBuffer(length);
        try
        {
            using var keyBytes = key.Unprotect();
            using var aes = new AesGcm(keyBytes.Span, 16);
            aes.Decrypt(
                nonce: blob.Slice(NonceOffset, 12),
                ciphertext: blob.Slice(HeaderLength, length),
                tag: blob.Slice(TagOffset, 16),
                plaintext: plaintext.Span,
                associatedData: aad);
            return plaintext;
        }
        catch (CryptographicException)
        {
            plaintext.Dispose();
            throw new VaultFormatException("No se pudo verificar el documento: está dañado o no pertenece a esta bóveda.");
        }
    }

    private static void BuildAad(ReadOnlySpan<byte> blob, Guid documentId, Span<byte> aad)
    {
        blob[..NonceOffset].CopyTo(aad);
        if (!documentId.TryWriteBytes(aad[NonceOffset..]))
        {
            throw new CryptographicException();
        }
    }
}
