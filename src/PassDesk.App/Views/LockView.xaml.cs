using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PassDesk.App.Infrastructure;
using PassDesk.Auth;
using PassDesk.Crypto;

namespace PassDesk.App.Views;

/// <summary>
/// Pantalla de bloqueo (contraseña maestra o Windows Hello) y de creación de la bóveda en el primer uso.
/// Las contraseñas se leen del PasswordBox como SecureString y se pasan a la derivación de claves
/// desde memoria fijada que se pone a cero; nunca se convierten en cadenas.
/// </summary>
public partial class LockView : UserControl
{
    private readonly DispatcherTimer _retryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AppServices? _services;
    private DateTimeOffset _retryAt;
    private UnlockStatus _retryStatus;
    private bool _busy;

    /// <summary>La contraseña se usa para recuperar la bóveda desde una copia de un guardado, no para desbloquearla.</summary>
    private bool _recovering;

    public LockView()
    {
        InitializeComponent();
        _retryTimer.Tick += (_, _) => UpdateRetryCountdown();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Refresh();
            }
        };
    }

    public void Initialize(AppServices services)
    {
        _services = services;
        services.Session.StateChanged += (_, _) => Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        if (_services is null)
        {
            return;
        }

        UnlockPassword.Clear();
        NewPassword.Clear();
        ConfirmPassword.Clear();
        var session = _services.Session;
        bool firstRun = session.State == VaultState.NotCreated;
        bool missing = session.RecoveryPending;
        _recovering = session.State == VaultState.Locked && (missing || (_recovering && session.CanRecover));
        SetupPanel.Visibility = firstRun ? Visibility.Visible : Visibility.Collapsed;
        UnlockPanel.Visibility = firstRun ? Visibility.Collapsed : Visibility.Visible;
        HeaderGlyph.Text = firstRun ? "" : "";
        Heading.Text = firstRun ? "Bienvenido a PassDesk" : _recovering ? "Recuperar la bóveda" : "PassDesk está bloqueado";
        SubHeading.Text = firstRun
            ? "Crea una contraseña maestra. Protegerá con cifrado las credenciales que importes en este equipo."
            : _recovering
                ? (missing ? "No se encuentra el archivo principal de la bóveda" : "El archivo principal de la bóveda no se puede abrir") +
                  ", pero queda una copia de un guardado anterior. Introduce tu contraseña maestra para recuperarla. " +
                  "Antes se guarda una copia de todo en la carpeta «recuperacion»: no se borra nada."
                : "Introduce tu contraseña maestra para continuar.";
        UnlockButton.Content = _recovering ? "Recuperar bóveda" : "Desbloquear";
        System.Windows.Automation.AutomationProperties.SetName(UnlockButton, (string)UnlockButton.Content);
        SetAsideButton.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        if (missing)
        {
            // No hay archivo principal que archivar: la alternativa a recuperar es apartar las copias.
            ArchiveButton.Visibility = Visibility.Collapsed;
        }

        _ = UpdateHelloButtonAsync();

        if (IsVisible)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => (firstRun ? NewPassword : UnlockPassword).Focus());
        }
    }

    private async Task UpdateHelloButtonAsync()
    {
        if (_services is null)
        {
            return;
        }

        bool show = !_recovering && _services.Settings.WindowsHelloQuickUnlock && _services.Session.CanQuickUnlock && await _services.Presence.IsAvailableAsync();
        HelloButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUnlockKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnUnlockClick(sender, e);
        }
    }

    private async void OnUnlockClick(object sender, RoutedEventArgs e)
    {
        if (_services is null || _busy || _retryTimer.IsEnabled)
        {
            return;
        }

        using var password = SecureStringHelper.ToSensitiveChars(UnlockPassword.SecurePassword);
        UnlockPassword.Clear();
        if (password.Length == 0)
        {
            ShowUnlockError("Introduce la contraseña maestra.");
            return;
        }

        SetBusy(true);
        bool recovering = _recovering;
        UnlockResult result;
        try
        {
            result = await Task.Run(() => recovering ? _services.Session.Recover(password.Span) : _services.Session.Unlock(password.Span));
        }
        catch (Exception ex) when (recovering && ex is IOException or UnauthorizedAccessException or VaultFormatException)
        {
            ShowUnlockError("No se pudo recuperar la bóveda. No se ha borrado nada: las copias siguen en la carpeta de datos de PassDesk. Inténtalo de nuevo.");
            return;
        }
        finally
        {
            SetBusy(false);
        }

        HandleUnlockResult(result);
    }

    private async void OnHelloClick(object sender, RoutedEventArgs e)
    {
        if (_services is null || _busy)
        {
            return;
        }

        SetBusy(true);
        UnlockResult result;
        try
        {
            result = await _services.Session.QuickUnlockAsync(_services.Presence, "Desbloquear PassDesk");
        }
        finally
        {
            SetBusy(false);
        }

        if (result.Status == UnlockStatus.NotAvailable)
        {
            ShowUnlockError("Windows Hello no está disponible ahora. Usa la contraseña maestra.");
            HelloButton.Visibility = Visibility.Collapsed;
        }
        else if (result.Status != UnlockStatus.Canceled)
        {
            HandleUnlockResult(result);
        }
    }

    private void HandleUnlockResult(UnlockResult result)
    {
        switch (result.Status)
        {
            case UnlockStatus.Success:
                UnlockError.Visibility = Visibility.Collapsed;
                break;
            case UnlockStatus.WrongPassword when result.RetryAfter > TimeSpan.Zero:
            case UnlockStatus.Throttled:
                _retryAt = DateTimeOffset.UtcNow + result.RetryAfter;
                _retryStatus = result.Status;
                _retryTimer.Start();
                UpdateRetryCountdown();
                break;
            case UnlockStatus.WrongPassword:
                ShowUnlockError("Contraseña maestra incorrecta.");
                break;
            case UnlockStatus.VaultUnreadable or UnlockStatus.VaultCorrupted when !_recovering && _services!.Session.CanRecover:
                // La principal no se puede abrir (dañada, a medias o de otra cuenta) pero quedan copias de un guardado: se
                // ofrece recuperarlas antes que empezar de cero. Recuperar no borra nada y comprueba la copia antes de usarla.
                _recovering = true;
                Refresh();
                ShowUnlockError("La bóveda principal no se puede abrir, pero queda una copia de un guardado anterior. Vuelve a escribir la contraseña maestra y pulsa «Recuperar bóveda». Si has cambiado de equipo o de usuario de Windows, restaura mejor una copia de seguridad (.pdbackup).");
                ShowArchiveOption();
                break;
            case UnlockStatus.VaultUnreadable:
                ShowUnlockError("Windows no puede descifrar la bóveda con esta cuenta de usuario. Si has cambiado de equipo o de usuario de Windows, restaura una copia de seguridad (.pdbackup) en una bóveda nueva.");
                ShowArchiveOption();
                break;
            case UnlockStatus.VaultCorrupted:
                ShowUnlockError(_recovering
                    ? "Las copias de la bóveda también están dañadas. Puedes restaurar una copia de seguridad (.pdbackup) en una bóveda nueva."
                    : "La bóveda está dañada o ha sido modificada fuera de PassDesk. Puedes restaurar una copia de seguridad.");
                ShowArchiveOption();
                break;
            case UnlockStatus.RecoveryRequired:
                _recovering = true;
                Refresh();
                ShowUnlockError("No se encuentra el archivo principal de la bóveda. Escribe la contraseña maestra y pulsa «Recuperar bóveda».");
                break;
            case UnlockStatus.CryptoUnavailable:
                ShowUnlockError(CryptoUnavailableException.UserMessage);
                break;
            default:
                ShowUnlockError("No se pudo desbloquear. Inténtalo de nuevo.");
                break;
        }

        if (result.Status != UnlockStatus.Success)
        {
            UnlockPassword.Focus();
        }
    }

    /// <summary>
    /// Archiva (sin borrarla) una bóveda ilegible para poder crear una nueva y restaurar una copia.
    /// </summary>
    private void OnArchiveVault(object sender, RoutedEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            "La bóveda actual NO se borrará: se renombrará y se conservará en la carpeta de datos de PassDesk.\n\n" +
            "Después podrás crear una bóveda nueva y restaurar tu copia de seguridad (.pdbackup) desde Ajustes.\n\n¿Continuar?",
            "Empezar con una bóveda nueva",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            string archived = System.IO.Path.Combine(_services.Paths.Root, $"vault-archivada-{DateTime.Now:yyyyMMdd-HHmmss}.pdv");
            System.IO.File.Move(_services.Paths.VaultFile, archived);
            ArchiveButton.Visibility = Visibility.Collapsed;
            UnlockError.Visibility = Visibility.Collapsed;
            _services.Session.RefreshStoreState();

            // Si quedan copias de un guardado, la sesión sigue bloqueada y se ofrece recuperarlas (o apartarlas).
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowUnlockError("No se pudo archivar la bóveda actual.");
        }
    }

    /// <summary>El archivo principal existe y no se puede abrir: se ofrece archivarlo (sin borrarlo).</summary>
    private void ShowArchiveOption() =>
        ArchiveButton.Visibility = _services!.Session.RecoveryPending ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Sin archivo principal, la alternativa a recuperar (por ejemplo, si no se recuerda la contraseña): mover las copias a la
    /// carpeta de recuperación, sin borrarlas, y crear una bóveda nueva.
    /// </summary>
    private void OnSetAsideCopies(object sender, RoutedEventArgs e)
    {
        if (_services is null || _busy)
        {
            return;
        }

        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            "Las copias de la bóveda NO se borrarán: se moverán a la carpeta «recuperacion» de PassDesk (archivos «conservada-…»).\n\n" +
            "Después podrás crear una bóveda nueva. Si más tarde recuerdas la contraseña, podrás restaurarlas desde Ajustes › Puntos de recuperación….\n\n¿Continuar?",
            "Apartar las copias y crear una bóveda nueva",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _services.Session.SetAsideRecoveryCopies();
            UnlockError.Visibility = Visibility.Collapsed;
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowUnlockError("No se pudieron apartar las copias. No se ha borrado nada.");
        }
    }

    private void UpdateRetryCountdown()
    {
        var remaining = _retryAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _retryTimer.Stop();
            UnlockButton.IsEnabled = true;
            ShowUnlockError("Puedes volver a intentarlo.");
            return;
        }

        UnlockButton.IsEnabled = false;
        ShowUnlockError(RetryCountdownText(_retryStatus, remaining));
    }

    /// <summary>Con una espera ya guardada (Throttled) la contraseña ni se comprueba: no se dice que sea incorrecta.</summary>
    internal static string RetryCountdownText(UnlockStatus status, TimeSpan remaining) =>
        $"{(status == UnlockStatus.WrongPassword ? "Contraseña incorrecta. " : string.Empty)}Demasiados intentos: espera {Math.Ceiling(remaining.TotalSeconds):0} s.";

    private void ShowUnlockError(string message)
    {
        UnlockError.Text = message;
        UnlockError.Visibility = Visibility.Visible;
    }

    private void OnSetupPasswordChanged(object sender, RoutedEventArgs e)
    {
        using var candidate = SecureStringHelper.ToSensitiveChars(NewPassword.SecurePassword);
        using var confirmation = SecureStringHelper.ToSensitiveChars(ConfirmPassword.SecurePassword);
        var strength = MasterPasswordPolicy.Evaluate(candidate.Span);

        int level = candidate.Length == 0 ? 0 : strength switch
        {
            PasswordStrength.TooShort => 1,
            PasswordStrength.Weak => 1,
            PasswordStrength.Fair => 2,
            PasswordStrength.Strong => 3,
            _ => 4,
        };
        Brush active = level switch
        {
            1 => (Brush)FindResource("Brush.Danger"),
            2 => (Brush)FindResource("Brush.Warning"),
            _ => (Brush)FindResource("Brush.Success"),
        };
        Brush inactive = (Brush)FindResource("Brush.Stroke");
        Border[] bars = [Strength1, Strength2, Strength3, Strength4];
        for (int i = 0; i < bars.Length; i++)
        {
            bars[i].Background = i < level ? active : inactive;
        }

        StrengthText.Text = candidate.Length == 0
            ? "Mínimo 12 caracteres y fortaleza «Fuerte». Mejor una frase de 4 o más palabras con algún número o símbolo."
            : strength switch
            {
                PasswordStrength.TooShort => $"Demasiado corta ({candidate.Length}/{MasterPasswordPolicy.MinLength} caracteres).",
                PasswordStrength.Weak => "Débil: añade más palabras, números o símbolos.",
                PasswordStrength.Fair => "Casi: añade otra palabra o algún número o símbolo para que sea «Fuerte».",
                PasswordStrength.Strong => "Fuerte.",
                _ => "Muy fuerte.",
            };

        bool matches = candidate.Length > 0 && candidate.Span.SequenceEqual(confirmation.Span);
        CreateButton.IsEnabled = MasterPasswordPolicy.IsAcceptable(candidate.Span) && matches && !_busy;
        SetupError.Visibility = confirmation.Length > 0 && !matches ? Visibility.Visible : Visibility.Collapsed;
        SetupError.Text = "Las contraseñas no coinciden.";
    }

    private void OnSetupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CreateButton.IsEnabled)
        {
            OnCreateClick(sender, e);
        }
    }

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (_services is null || _busy)
        {
            return;
        }

        using var password = SecureStringHelper.ToSensitiveChars(NewPassword.SecurePassword);
        using var confirmation = SecureStringHelper.ToSensitiveChars(ConfirmPassword.SecurePassword);
        if (!password.Span.SequenceEqual(confirmation.Span))
        {
            SetupError.Text = "Las contraseñas no coinciden.";
            SetupError.Visibility = Visibility.Visible;
            return;
        }

        SetBusy(true);
        UnlockResult result;
        try
        {
            result = await Task.Run(() => _services.Session.CreateVault(password.Span));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = new UnlockResult(UnlockStatus.VaultUnreadable);
        }
        finally
        {
            SetBusy(false);
        }

        if (!result.Succeeded)
        {
            SetupError.Text = result.Status switch
            {
                UnlockStatus.WeakPassword => "La contraseña no cumple los requisitos mínimos.",
                UnlockStatus.CryptoUnavailable => CryptoUnavailableException.UserMessage,
                _ => "No se pudo crear la bóveda en este equipo.",
            };
            SetupError.Visibility = Visibility.Visible;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UnlockButton.IsEnabled = !busy && !_retryTimer.IsEnabled;
        HelloButton.IsEnabled = !busy;
        UnlockPassword.IsEnabled = !busy;
        if (busy)
        {
            CreateButton.IsEnabled = false;
            UnlockError.Visibility = Visibility.Collapsed;
        }
    }
}
