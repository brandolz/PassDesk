using System.Security.Cryptography;

namespace PassDesk.Browser.Protocol;

/// <summary>
/// Búfer en memoria fijada (no lo mueve el recolector de basura) que se pone a cero al liberarlo. Contiene mensajes
/// que pueden llevar contraseñas, así que su vida debe ser la mínima.
/// </summary>
public sealed class PinnedBuffer : IDisposable
{
    private byte[]? _data;

    public PinnedBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _data = GC.AllocateArray<byte>(Math.Max(capacity, 1), pinned: true);
    }

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Span => (_data ?? throw new ObjectDisposedException(nameof(PinnedBuffer))).AsSpan(0, Length);

    public ReadOnlyMemory<byte> Memory => (_data ?? throw new ObjectDisposedException(nameof(PinnedBuffer))).AsMemory(0, Length);

    /// <summary>Memoria completa para escribir (por ejemplo, al leer de una tubería). Después, <see cref="SetLength"/>.</summary>
    public Memory<byte> Writable => (_data ?? throw new ObjectDisposedException(nameof(PinnedBuffer))).AsMemory();

    public int Capacity => _data?.Length ?? 0;

    public void SetLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, Capacity);
        Length = length;
    }

    /// <summary>Añade bytes, ampliando el búfer si hace falta; el búfer anterior se pone a cero.</summary>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        var data = _data ?? throw new ObjectDisposedException(nameof(PinnedBuffer));
        if (Length + bytes.Length > data.Length)
        {
            var bigger = GC.AllocateArray<byte>(Math.Max(data.Length * 2, Length + bytes.Length), pinned: true);
            data.AsSpan(0, Length).CopyTo(bigger);
            CryptographicOperations.ZeroMemory(data);
            _data = data = bigger;
        }

        bytes.CopyTo(data.AsSpan(Length));
        Length += bytes.Length;
    }

    public void Append(byte value)
    {
        Span<byte> one = [value];
        Append(one);
    }

    public void Dispose()
    {
        if (_data is { } data)
        {
            CryptographicOperations.ZeroMemory(data);
            _data = null;
            Length = 0;
        }
    }
}
