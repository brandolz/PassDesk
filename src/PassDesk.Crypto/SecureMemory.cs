using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PassDesk.Crypto;

/// <summary>
/// Utilidades para manejar memoria que contiene material sensible.
/// Los búferes se reservan en el montón de objetos fijados (POH) para que el
/// recolector de basura nunca deje copias del contenido al compactar la memoria.
/// </summary>
public static class SecureMemory
{
    public static byte[] AllocatePinned(int length) =>
        length == 0 ? [] : GC.AllocateArray<byte>(length, pinned: true);

    public static char[] AllocatePinnedChars(int length) =>
        length == 0 ? [] : GC.AllocateArray<char>(length, pinned: true);

    public static void Zero(Span<byte> buffer) => CryptographicOperations.ZeroMemory(buffer);

    public static void Zero(Span<char> buffer) =>
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer));
}

/// <summary>Búfer de bytes fijado en memoria que se pone a cero al liberarse.</summary>
public sealed class SensitiveBuffer : IDisposable
{
    private byte[]? _data;
    private readonly int _offset;

    public SensitiveBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _data = SecureMemory.AllocatePinned(length);
        Length = length;
    }

    /// <summary>Toma posesión de un array ya fijado; se pondrá a cero completo al liberar.</summary>
    internal SensitiveBuffer(byte[] pinnedData, int offset, int length)
    {
        _data = pinnedData;
        _offset = offset;
        Length = length;
    }

    public int Length { get; }

    public Span<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_data is null, this);
            return _data.AsSpan(_offset, Length);
        }
    }

    /// <summary>
    /// Flujo de solo lectura sobre el contenido, sin copiarlo (por ejemplo, para decodificar una imagen).
    /// No debe usarse después de liberar el búfer.
    /// </summary>
    public Stream AsReadOnlyStream()
    {
        ObjectDisposedException.ThrowIf(_data is null, this);
        return new MemoryStream(_data, _offset, Length, writable: false);
    }

    public void Dispose()
    {
        if (_data is not null)
        {
            SecureMemory.Zero(_data);
            _data = null;
        }
    }

    public override string ToString() => "[redacted]";
}

/// <summary>Búfer de caracteres fijado en memoria que se pone a cero al liberarse.</summary>
public sealed class SensitiveChars : IDisposable
{
    private char[]? _data;

    public SensitiveChars(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _data = SecureMemory.AllocatePinnedChars(length);
        Length = length;
    }

    public int Length { get; private set; }

    public Span<char> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_data is null, this);
            return _data.AsSpan(0, Length);
        }
    }

    /// <summary>Reduce la longitud visible (por ejemplo, tras decodificar menos caracteres de los reservados).</summary>
    public void Truncate(int length)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, Length);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
    }

    public void Dispose()
    {
        if (_data is not null)
        {
            SecureMemory.Zero(_data);
            _data = null;
        }
    }

    public override string ToString() => "[redacted]";
}

/// <summary>
/// <see cref="IBufferWriter{T}"/> que trabaja sobre memoria fijada y pone a cero
/// cualquier búfer antiguo al crecer y todo el contenido al liberarse.
/// Se usa para serializar el contenido de la bóveda sin dejar restos en el montón.
/// </summary>
public sealed class SecureBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _written;

    public SecureBufferWriter(int initialCapacity = 16 * 1024)
    {
        _buffer = SecureMemory.AllocatePinned(Math.Max(256, initialCapacity));
    }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public int WrittenCount => _written;

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_written + count > _buffer.Length)
        {
            throw new InvalidOperationException("Advance excede la capacidad del búfer.");
        }

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void Ensure(int sizeHint)
    {
        if (sizeHint < 1)
        {
            sizeHint = 1;
        }

        if (_buffer.Length - _written >= sizeHint)
        {
            return;
        }

        int newSize = Math.Max(_buffer.Length * 2, _written + sizeHint);
        var replacement = SecureMemory.AllocatePinned(newSize);
        _buffer.AsSpan(0, _written).CopyTo(replacement);
        SecureMemory.Zero(_buffer);
        _buffer = replacement;
    }

    public void Dispose()
    {
        SecureMemory.Zero(_buffer);
        _written = 0;
    }
}
