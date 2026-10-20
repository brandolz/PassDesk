namespace PassDesk.Core.Services;

/// <summary>Reloj inyectable para poder probar bloqueos e intervalos sin esperar.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Programa una acción diferida (en la interfaz se ejecuta en el hilo de la UI).</summary>
public interface IDelayScheduler
{
    IDisposable Schedule(TimeSpan delay, Action callback);
}

/// <summary>Acceso al portapapeles del sistema.</summary>
public interface IClipboardService
{
    /// <summary>
    /// Copia texto marcándolo como sensible: excluido del historial del portapapeles (Win+V)
    /// y de la sincronización en la nube del portapapeles de Windows.
    /// </summary>
    void SetSensitiveText(string text);

    /// <summary>Número de secuencia del portapapeles; cambia cada vez que alguien lo modifica.</summary>
    uint GetSequenceNumber();

    void Clear();
}
