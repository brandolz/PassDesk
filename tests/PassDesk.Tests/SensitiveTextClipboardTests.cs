using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PassDesk.App.Infrastructure;
using PassDesk.Core.Services;

namespace PassDesk.Tests;

/// <summary>
/// Copiar o cortar en los cuadros de texto sensibles debe pasar por <see cref="ClipboardGuard"/> y nunca por el
/// portapapeles normal de WPF. Como red de seguridad, cada prueba cancela cualquier copia estándar que llegara a
/// intentarse, así que el portapapeles real del equipo nunca se toca.
/// </summary>
public class SensitiveTextClipboardTests
{
    [Fact]
    public void Copy_from_a_sensitive_text_box_goes_through_the_guard()
    {
        Sta.Run(() =>
        {
            var clipboard = new FakeClipboard();
            using var guard = new ClipboardGuard(clipboard, new ManualScheduler(), () => TimeSpan.FromSeconds(30));
            var box = new TextBox { Text = "Código de recuperación ficticio 1234-5678", IsReadOnly = true };
            GuardAgainstStandardCopy(box);
            TimeSpan? copied = null;
            SensitiveTextClipboard.Attach(box, guard, ClipboardItemKind.Note, delay => copied = delay);

            box.SelectAll();
            ApplicationCommands.Copy.Execute(null, box);

            Assert.False(StandardCopyAttempted(box));
            Assert.Equal(box.Text, clipboard.Text);
            Assert.True(clipboard.LastSetWasSensitive);
            Assert.True(guard.OwnsClipboard);
            Assert.Equal(TimeSpan.FromSeconds(30), copied);
        });
    }

    [Fact]
    public void Cut_from_an_editable_box_copies_through_the_guard_and_removes_the_selection()
    {
        Sta.Run(() =>
        {
            var clipboard = new FakeClipboard();
            using var guard = new ClipboardGuard(clipboard, new ManualScheduler(), () => TimeSpan.FromSeconds(30));
            var box = new TextBox { Text = Fake.Secret };
            GuardAgainstStandardCopy(box);
            SensitiveTextClipboard.Attach(box, guard, ClipboardItemKind.Password);

            box.SelectAll();
            ApplicationCommands.Cut.Execute(null, box);

            Assert.False(StandardCopyAttempted(box));
            Assert.Equal(Fake.Secret, clipboard.Text);
            Assert.Equal(string.Empty, box.Text);
        });
    }

    [Fact]
    public void Only_the_selected_text_is_copied_and_empty_selection_copies_nothing()
    {
        Sta.Run(() =>
        {
            var clipboard = new FakeClipboard();
            using var guard = new ClipboardGuard(clipboard, new ManualScheduler(), () => TimeSpan.FromSeconds(30));
            var box = new TextBox { Text = "PIN ficticio: 4321", IsReadOnly = true };
            GuardAgainstStandardCopy(box);
            SensitiveTextClipboard.Attach(box, guard, ClipboardItemKind.Note);

            ApplicationCommands.Copy.Execute(null, box); // sin selección
            Assert.Null(clipboard.Text);

            box.Select(14, 4);
            ApplicationCommands.Copy.Execute(null, box);
            Assert.Equal("4321", clipboard.Text);
            Assert.False(StandardCopyAttempted(box));
        });
    }

    [Fact]
    public void Control_without_protection_wpf_would_use_the_standard_clipboard()
    {
        // Demuestra que las pruebas anteriores detectarían el fallo: sin Attach, WPF intenta la copia estándar
        // (que aquí se cancela, así que el portapapeles real del equipo no cambia).
        Sta.Run(() =>
        {
            uint before = GetClipboardSequenceNumber();
            var box = new TextBox { Text = "texto ficticio de control", IsReadOnly = true };
            GuardAgainstStandardCopy(box);

            box.SelectAll();
            ApplicationCommands.Copy.Execute(null, box);

            Assert.True(StandardCopyAttempted(box));
            Assert.Equal(before, GetClipboardSequenceNumber());
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private static readonly DependencyProperty StandardCopyProperty =
        DependencyProperty.RegisterAttached("StandardCopy", typeof(bool), typeof(SensitiveTextClipboardTests));

    /// <summary>Cancela (y anota) cualquier copia estándar de WPF, que usaría el portapapeles real.</summary>
    private static void GuardAgainstStandardCopy(TextBox box) =>
        DataObject.AddCopyingHandler(box, (_, e) =>
        {
            box.SetValue(StandardCopyProperty, true);
            e.CancelCommand();
        });

    private static bool StandardCopyAttempted(TextBox box) => (bool)box.GetValue(StandardCopyProperty);
}
