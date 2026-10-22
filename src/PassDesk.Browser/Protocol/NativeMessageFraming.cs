using System.Buffers.Binary;

namespace PassDesk.Browser.Protocol;

public enum FrameStatus
{
    Ok,

    /// <summary>El otro extremo cerró sin enviar nada.</summary>
    EndOfStream,

    /// <summary>La longitud anunciada supera el límite o es cero: no se lee el cuerpo.</summary>
    InvalidLength,

    /// <summary>El flujo se cortó a mitad del mensaje.</summary>
    Truncated,
}

/// <summary>
/// Formato de mensaje de Native Messaging: longitud de 4 bytes (little-endian) seguida de JSON en UTF-8. Se usa igual
/// en la entrada y salida estándar del conector y en la tubería local de PassDesk.
/// </summary>
public static class NativeMessageFraming
{
    /// <summary>
    /// Lee un mensaje. Si la longitud anunciada es cero o supera <paramref name="maxBytes"/>, devuelve
    /// <see cref="FrameStatus.InvalidLength"/> sin leer el cuerpo. El llamador libera el búfer devuelto.
    /// </summary>
    public static async Task<(FrameStatus Status, PinnedBuffer? Buffer)> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[4];
        int read = await ReadFullyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return (FrameStatus.EndOfStream, null);
        }

        if (read < header.Length)
        {
            return (FrameStatus.Truncated, null);
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > (uint)maxBytes)
        {
            return (FrameStatus.InvalidLength, null);
        }

        var buffer = new PinnedBuffer((int)length);
        try
        {
            int body = await ReadFullyAsync(stream, buffer.Writable[..(int)length], cancellationToken).ConfigureAwait(false);
            if (body < length)
            {
                buffer.Dispose();
                return (FrameStatus.Truncated, null);
            }

            buffer.SetLength((int)length);
            return (FrameStatus.Ok, buffer);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadFullyAsync(Stream stream, Memory<byte> destination, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = await stream.ReadAsync(destination[total..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
