namespace PassDesk.App.Infrastructure;

/// <summary>
/// Retardo de armado de la confirmación del autorrelleno: «Permitir» solo vale cuando la ventana lleva un tiempo seguido
/// activa. Al abrirse, al perder el foco o al recuperarlo se empieza de cero, así que una tecla o un clic que llegan justo
/// cuando la ventana aparece (o cuando alguien le devuelve el foco) no autorizan nada.
/// </summary>
internal sealed class ConfirmArming(TimeProvider time, TimeSpan delay)
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(1);

    private long? _activeSince;

    public bool IsArmed => _activeSince is long since && time.GetElapsedTime(since) >= delay;

    public void Activated() => _activeSince = time.GetTimestamp();

    public void Deactivated() => _activeSince = null;
}
