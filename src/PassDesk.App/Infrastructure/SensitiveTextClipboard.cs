using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using PassDesk.Core.Services;

namespace PassDesk.App.Infrastructure;

/// <summary>
/// Copiar y Cortar en un cuadro de texto con datos sensibles (notas, contraseña visible, texto de un
/// documento, contraseña generada) pasan por <see cref="ClipboardGuard"/>: el contenido queda excluido del
/// historial de Win+V y de la nube y se borra solo. Sin esto, WPF usaría el portapapeles normal.
/// Cubre Ctrl+C, Ctrl+X, Ctrl+Insert, Mayús+Supr y el menú contextual, que usan los mismos comandos.
/// </summary>
internal static class SensitiveTextClipboard
{
    public static void Attach(TextBox box, ClipboardGuard guard, ClipboardItemKind kind, Action<TimeSpan>? copied = null, Action? failed = null)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(guard);
        CommandManager.AddPreviewExecutedHandler(box, (_, e) =>
        {
            bool cut = e.Command == ApplicationCommands.Cut;
            if (!cut && e.Command != ApplicationCommands.Copy)
            {
                return;
            }

            // Se marca como atendido antes de nada: el comando estándar de WPF no llega a ejecutarse.
            e.Handled = true;
            string selected = box.SelectedText;
            if (selected.Length == 0)
            {
                return;
            }

            try
            {
                var delay = guard.Copy(selected, kind);
                if (cut && !box.IsReadOnly)
                {
                    box.SelectedText = string.Empty;
                }

                copied?.Invoke(delay);
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                failed?.Invoke();
            }
        });
    }
}
