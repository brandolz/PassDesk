using PassDesk.App.Infrastructure;

namespace PassDesk.Tests;

/// <summary>
/// Retardo de armado de la ventana de confirmación del autorrelleno: «Permitir» solo vale tras un segundo seguido con la
/// ventana activa, y cada pérdida o recuperación del foco empieza de cero. El comportamiento con teclado y ratón reales
/// de la ventana WPF se comprueba a mano en Windows.
/// </summary>
public class ConfirmArmingTests
{
    private static readonly TimeSpan Delay = ConfirmArming.DefaultDelay;
    private static readonly TimeSpan JustUnder = Delay - TimeSpan.FromMilliseconds(1);

    [Fact]
    public void The_default_delay_is_one_second()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), ConfirmArming.DefaultDelay);
    }

    [Fact]
    public void A_window_that_was_just_opened_is_not_armed_even_after_a_while_without_focus()
    {
        var time = new ManualTime();
        var arming = new ConfirmArming(time, Delay);
        Assert.False(arming.IsArmed);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(arming.IsArmed);
    }

    [Fact]
    public void It_arms_only_after_a_full_second_of_activation()
    {
        var time = new ManualTime();
        var arming = new ConfirmArming(time, Delay);
        arming.Activated();
        Assert.False(arming.IsArmed);
        time.Advance(JustUnder);
        Assert.False(arming.IsArmed);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(arming.IsArmed);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(arming.IsArmed);
    }

    [Fact]
    public void Losing_the_focus_disarms_at_once()
    {
        var time = new ManualTime();
        var arming = new ConfirmArming(time, Delay);
        arming.Activated();
        time.Advance(Delay);
        Assert.True(arming.IsArmed);
        arming.Deactivated();
        Assert.False(arming.IsArmed);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(arming.IsArmed);
    }

    [Fact]
    public void Getting_the_focus_back_restarts_the_full_delay()
    {
        var time = new ManualTime();
        var arming = new ConfirmArming(time, Delay);
        arming.Activated();
        time.Advance(Delay);
        arming.Deactivated();
        time.Advance(TimeSpan.FromSeconds(10));

        arming.Activated();
        Assert.False(arming.IsArmed);
        time.Advance(JustUnder);
        Assert.False(arming.IsArmed);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(arming.IsArmed);
    }

    [Fact]
    public void Time_spent_active_before_a_focus_loss_does_not_count()
    {
        var time = new ManualTime();
        var arming = new ConfirmArming(time, Delay);
        arming.Activated();
        time.Advance(JustUnder);
        arming.Deactivated();
        arming.Activated();
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(arming.IsArmed);
    }

    [Fact]
    public void A_repeated_activation_event_also_restarts_the_delay()
    {
        var time = new ManualTime();
        var arming = new ConfirmArming(time, Delay);
        arming.Activated();
        time.Advance(JustUnder);
        arming.Activated();
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(arming.IsArmed);
        time.Advance(JustUnder);
        Assert.True(arming.IsArmed);
    }

    /// <summary>Reloj manual (en milisegundos) para probar el retardo sin esperar.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = 1_000_000;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += (long)by.TotalMilliseconds;
    }
}
