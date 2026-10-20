using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PassDesk.Crypto;

/// <summary>
/// Cifrado de memoria del propio proceso mediante <c>CryptProtectMemory</c> de Windows
/// (indicador SAME_PROCESS: solo este proceso puede descifrar el contenido).
/// </summary>
internal static unsafe partial class MemoryProtection
{
    public const int BlockSize = 16; // CRYPTPROTECTMEMORY_BLOCK_SIZE
    private const uint SameProcess = 0x00; // CRYPTPROTECTMEMORY_SAME_PROCESS

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectMemory(byte* pData, uint cbData, uint dwFlags);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectMemory(byte* pData, uint cbData, uint dwFlags);

    public static int RoundUp(int length) => (length + BlockSize - 1) / BlockSize * BlockSize;

    public static void Protect(Span<byte> buffer)
    {
        Validate(buffer);
        fixed (byte* p = buffer)
        {
            if (!CryptProtectMemory(p, (uint)buffer.Length, SameProcess))
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }
        }
    }

    public static void Unprotect(Span<byte> buffer)
    {
        Validate(buffer);
        fixed (byte* p = buffer)
        {
            if (!CryptUnprotectMemory(p, (uint)buffer.Length, SameProcess))
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }
        }
    }

    private static void Validate(Span<byte> buffer)
    {
        if (buffer.Length == 0 || buffer.Length % BlockSize != 0)
        {
            throw new ArgumentException("El tamaño debe ser múltiplo del bloque de protección.", nameof(buffer));
        }
    }
}

/// <summary>
/// Secreto (contraseña, nota, semilla TOTP o clave) mantenido cifrado en memoria.
/// <para>
/// El texto en claro solo existe de forma transitoria en búferes fijados que se ponen a cero
/// tras su uso. <see cref="ToString"/> nunca devuelve el contenido, de modo que un secreto
/// interpolado por error en un registro o mensaje de error aparece como <c>[redacted]</c>.
/// </para>
/// </summary>
[DebuggerDisplay("[redacted]")]
public sealed class ProtectedSecret : IDisposable
{
    /// <summary>Secreto vacío compartido. Liberarlo no tiene efecto.</summary>
    public static ProtectedSecret Empty { get; } = new();

    // Diseño: [longitud int32][bytes en claro][relleno] cifrado con CryptProtectMemory.
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private byte[]? _protected;
    private readonly Lock _gate = new();

    private ProtectedSecret()
    {
        IsEmpty = true;
    }

    private ProtectedSecret(ReadOnlySpan<byte> plaintext)
    {
        int total = MemoryProtection.RoundUp(sizeof(int) + plaintext.Length);
        var buffer = SecureMemory.AllocatePinned(total);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, plaintext.Length);
            plaintext.CopyTo(buffer.AsSpan(sizeof(int)));
            MemoryProtection.Protect(buffer);
        }
        catch
        {
            SecureMemory.Zero(buffer);
            throw;
        }

        _protected = buffer;
    }

    public bool IsEmpty { get; }

    public bool IsDisposed { get; private set; }

    public static ProtectedSecret FromBytes(ReadOnlySpan<byte> plaintext) =>
        plaintext.IsEmpty ? Empty : new ProtectedSecret(plaintext);

    public static ProtectedSecret FromChars(ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty)
        {
            return Empty;
        }

        using var utf8 = new SensitiveBuffer(Encoding.UTF8.GetMaxByteCount(chars.Length));
        int written = Encoding.UTF8.GetBytes(chars, utf8.Span);
        return new ProtectedSecret(utf8.Span[..written]);
    }

    /// <summary>
    /// Crea un secreto a partir de una cadena gestionada. Solo debe usarse cuando la cadena ya
    /// existe por necesidad (por ejemplo, un campo de texto de la interfaz).
    /// </summary>
    public static ProtectedSecret FromString(string? value) =>
        string.IsNullOrEmpty(value) ? Empty : FromChars(value);

    /// <summary>
    /// Descifra el secreto en un búfer fijado. El llamador debe liberarlo (se pone a cero) cuanto antes.
    /// </summary>
    public SensitiveBuffer Unprotect()
    {
        if (IsEmpty)
        {
            return new SensitiveBuffer(0);
        }

        byte[] copy;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            copy = SecureMemory.AllocatePinned(_protected!.Length);
            _protected.CopyTo(copy, 0);
        }

        try
        {
            MemoryProtection.Unprotect(copy);
            int length = BinaryPrimitives.ReadInt32LittleEndian(copy);
            if (length < 0 || length > copy.Length - sizeof(int))
            {
                throw new CryptographicException("Secreto en memoria inválido.");
            }

            return new SensitiveBuffer(copy, sizeof(int), length);
        }
        catch
        {
            SecureMemory.Zero(copy);
            throw;
        }
    }

    /// <summary>
    /// Devuelve el secreto como cadena gestionada. Solo para mostrarlo o copiarlo a petición del usuario;
    /// las cadenas de .NET no se pueden borrar, así que su uso debe ser breve.
    /// </summary>
    public string Reveal()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        using var plain = Unprotect();
        return Encoding.UTF8.GetString(plain.Span);
    }

    /// <summary>Compara el contenido de dos secretos en tiempo constante (respecto al contenido).</summary>
    public bool ContentEquals(ProtectedSecret other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (IsEmpty || other.IsEmpty)
        {
            return IsEmpty && other.IsEmpty;
        }

        using var a = Unprotect();
        using var b = other.Unprotect();
        return CryptographicOperations.FixedTimeEquals(a.Span, b.Span);
    }

    public ProtectedSecret Clone()
    {
        if (IsEmpty)
        {
            return Empty;
        }

        using var plain = Unprotect();
        return FromBytes(plain.Span);
    }

    public void Dispose()
    {
        if (IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (_protected is not null)
            {
                SecureMemory.Zero(_protected);
                _protected = null;
            }

            IsDisposed = true;
        }
    }

    public override string ToString() => "[redacted]";
}
