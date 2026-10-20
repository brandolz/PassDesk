using System.Windows;
using PassDesk.App.Infrastructure;
using PassDesk.Auth;
using PassDesk.Crypto;

namespace PassDesk.App.Views;

/// <summary>
/// Diálogo de contraseña: pide la contraseña de una copia de seguridad o cambia la contraseña maestra.
/// La operación protegida se ejecuta dentro del diálogo para que la contraseña no salga de él.
/// </summary>
public partial class PasswordDialog : Window
{
    private readonly Func<SensitiveChars, SensitiveChars?, Task<string?>> _action;
    private readonly bool _changeMode;

    private PasswordDialog(AppServices services, bool changeMode, Func<SensitiveChars, SensitiveChars?, Task<string?>> action)
    {
        InitializeComponent();
        _changeMode = changeMode;
        _action = action;
        ChangePanel.Visibility = changeMode ? Visibility.Visible : Visibility.Collapsed;
        WindowSecurity.Protect(this, services);
        Loaded += (_, _) => FirstBox.Focus();
        Closed += (_, _) =>
        {
            FirstBox.Clear();
            NewBox.Clear();
            ConfirmBox.Clear();
        };
    }

    /// <summary>
    /// Pide una contraseña y ejecuta <paramref name="action"/> con ella. La acción devuelve null si tuvo
    /// éxito o un mensaje de error genérico para mostrar.
    /// </summary>
    public static bool Prompt(Window owner, AppServices services, string heading, string description, string label, Func<SensitiveChars, Task<string?>> action)
    {
        var dialog = new PasswordDialog(services, changeMode: false, (password, _) => action(password)) { Owner = owner };
        dialog.Heading.Text = heading;
        dialog.Description.Text = description;
        dialog.FirstLabel.Text = label;
        return dialog.ShowDialog() == true;
    }

    public static bool ChangeMasterPassword(Window owner, AppServices services)
    {
        var dialog = new PasswordDialog(services, changeMode: true, async (current, next) =>
        {
            var result = await Task.Run(() => services.Session.ChangeMasterPassword(current.Span, next!.Span));
            return result.Status switch
            {
                UnlockStatus.Success => null,
                UnlockStatus.WrongPassword => "La contraseña actual no es correcta.",
                UnlockStatus.Throttled => $"Demasiados intentos. Espera {Math.Ceiling(result.RetryAfter.TotalSeconds):0} s.",
                UnlockStatus.WeakPassword => "La nueva contraseña es demasiado débil (mínimo 12 caracteres y fortaleza «Fuerte»).",
                UnlockStatus.CryptoUnavailable => CryptoUnavailableException.UserMessage,
                _ => "No se pudo cambiar la contraseña.",
            };
        })
        { Owner = owner };
        dialog.Heading.Text = "Cambiar contraseña maestra";
        dialog.Description.Text = "La bóveda se volverá a cifrar con una clave nueva protegida por la nueva contraseña.";
        dialog.FirstLabel.Text = "Contraseña maestra actual";
        return dialog.ShowDialog() == true;
    }

    private async void OnAccept(object sender, RoutedEventArgs e)
    {
        using var first = SecureStringHelper.ToSensitiveChars(FirstBox.SecurePassword);
        using var next = _changeMode ? SecureStringHelper.ToSensitiveChars(NewBox.SecurePassword) : null;
        using var confirm = _changeMode ? SecureStringHelper.ToSensitiveChars(ConfirmBox.SecurePassword) : null;

        if (first.Length == 0)
        {
            ShowError("Introduce la contraseña.");
            return;
        }

        if (_changeMode && !next!.Span.SequenceEqual(confirm!.Span))
        {
            ShowError("Las contraseñas nuevas no coinciden.");
            return;
        }

        AcceptButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        string? error;
        try
        {
            error = await _action(first, next);
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            AcceptButton.IsEnabled = true;
        }

        if (error is null)
        {
            DialogResult = true;
            return;
        }

        FirstBox.Clear();
        ShowError(error);
        FirstBox.Focus();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
