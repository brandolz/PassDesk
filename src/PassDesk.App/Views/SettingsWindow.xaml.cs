using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PassDesk.App.Infrastructure;
using PassDesk.Auth;
using PassDesk.Core.Diagnostics;
using PassDesk.Core.Model;
using PassDesk.Crypto;
using PassDesk.Import;
using PassDesk.Storage;

namespace PassDesk.App.Views;

public sealed record Choice(int Value, string Label);

public partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    private HotkeyGesture? _hotkey;
    private bool _startupEnabled;

    public SettingsWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        WindowSecurity.Protect(this, services);
        var settings = services.Settings;

        var autoLock = AppSettings.AutoLockChoices.Select(m => new Choice(m, m == 1 ? "1 minuto" : $"{m} minutos")).ToList();
        AutoLockBox.ItemsSource = autoLock;
        AutoLockBox.DisplayMemberPath = nameof(Choice.Label);
        AutoLockBox.SelectedItem = autoLock.MinBy(c => Math.Abs(c.Value - settings.AutoLockMinutes));

        var clipboard = AppSettings.ClipboardChoices.Select(s => new Choice(s, $"{s} segundos")).ToList();
        ClipboardBox.ItemsSource = clipboard;
        ClipboardBox.DisplayMemberPath = nameof(Choice.Label);
        ClipboardBox.SelectedItem = clipboard.MinBy(c => Math.Abs(c.Value - settings.ClipboardClearSeconds));

        HelloCheck.IsChecked = settings.WindowsHelloQuickUnlock;
        SessionLockCheck.IsChecked = settings.LockOnSessionLock;
        MinimizeLockCheck.IsChecked = settings.LockOnMinimize;
        CaptureCheck.IsChecked = settings.ScreenCaptureProtection;
        ThemeSystem.IsChecked = settings.Theme == ThemePreference.System;
        ThemeLight.IsChecked = settings.Theme == ThemePreference.Light;
        ThemeDark.IsChecked = settings.Theme == ThemePreference.Dark;
        HotkeyCheck.IsChecked = settings.QuickSearchEnabled;
        DetectExportsCheck.IsChecked = settings.DetectNewExports;
        DeleteExportCheck.IsChecked = settings.DeleteExportAfterImport;
        ReminderCheck.IsChecked = settings.UpdateReminder;
        TrayOnClose.IsChecked = settings.TrayMode == TrayMode.CloseToTray;
        TrayOnMinimize.IsChecked = settings.TrayMode == TrayMode.MinimizeToTray;
        _startupEnabled = services.Startup.IsEnabled;
        StartupCheck.IsChecked = _startupEnabled;
        if (!services.Startup.IsAvailable)
        {
            StartupCheck.IsEnabled = false;
            StartupStatus.Text = "Solo está disponible en la copia instalada con scripts\\install.ps1 (en %LOCALAPPDATA%\\Programs\\PassDesk) y con la carpeta de datos por defecto.";
        }

        if (HotkeyGesture.TryParse(settings.QuickSearchHotkey, out var gesture))
        {
            _hotkey = gesture;
            HotkeyBox.Text = gesture.ToDisplayString();
        }

        if (settings.QuickSearchEnabled && services.Hotkeys?.Current is null)
        {
            HotkeyStatus.Text = "No se pudo registrar el atajo: puede que otra aplicación ya lo use. Elige otra combinación.";
            HotkeyStatus.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Danger");
        }

        DataFolder.Text = services.Paths.Root;
        _ = CheckHelloAsync();

        services.Session.StateChanged += OnSessionStateChanged;
        Closed += (_, _) => services.Session.StateChanged -= OnSessionStateChanged;
    }

    private void OnSessionStateChanged(object? sender, VaultStateChangedEventArgs e)
    {
        if (e.NewState != VaultState.Unlocked)
        {
            Dispatcher.BeginInvoke(Close);
        }
    }

    private async Task CheckHelloAsync()
    {
        if (!await _services.Presence.IsAvailableAsync())
        {
            HelloStatus.Text = "Windows Hello no está configurado o no está disponible en este equipo (Configuración › Cuentas › Opciones de inicio de sesión).";
        }
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HotkeyGesture.IsModifierKey(key) || key is Key.Tab)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            HotkeyStatus.Text = "El atajo debe incluir Ctrl, Alt, Mayús o Win.";
            return;
        }

        _hotkey = new HotkeyGesture(modifiers, key);
        HotkeyBox.Text = _hotkey.Value.ToDisplayString();
        HotkeyStatus.Text = "Se aplicará al guardar.";
        HotkeyStatus.ClearValue(ForegroundProperty);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var settings = _services.Settings.Clone();
        settings.AutoLockMinutes = (AutoLockBox.SelectedItem as Choice)?.Value ?? settings.AutoLockMinutes;
        settings.ClipboardClearSeconds = (ClipboardBox.SelectedItem as Choice)?.Value ?? settings.ClipboardClearSeconds;
        settings.WindowsHelloQuickUnlock = HelloCheck.IsChecked == true;
        settings.LockOnSessionLock = SessionLockCheck.IsChecked == true;
        settings.LockOnMinimize = MinimizeLockCheck.IsChecked == true;
        settings.ScreenCaptureProtection = CaptureCheck.IsChecked == true;
        settings.Theme = ThemeDark.IsChecked == true ? ThemePreference.Dark : ThemeLight.IsChecked == true ? ThemePreference.Light : ThemePreference.System;
        settings.QuickSearchEnabled = HotkeyCheck.IsChecked == true;
        settings.DetectNewExports = DetectExportsCheck.IsChecked == true;
        settings.DeleteExportAfterImport = DeleteExportCheck.IsChecked == true;
        settings.UpdateReminder = ReminderCheck.IsChecked == true;
        settings.TrayMode = TrayOnMinimize.IsChecked == true ? TrayMode.MinimizeToTray : TrayMode.CloseToTray;
        if (_hotkey is { } hotkey)
        {
            settings.QuickSearchHotkey = hotkey.ToString();
        }

        try
        {
            _services.UpdateSettings(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "No se pudieron guardar los ajustes.", "PassDesk", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // El registro solo se toca si se ha cambiado la casilla.
        bool startup = StartupCheck.IsChecked == true;
        if (startup != _startupEnabled)
        {
            if (!_services.Startup.TrySetEnabled(startup))
            {
                MessageBox.Show(this, "Los ajustes se han guardado, pero no se pudo cambiar «Iniciar con Windows»: puede que un antivirus o una directiva del equipo lo impidan.",
                    "PassDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _startupEnabled = startup;
        }

        if (settings.QuickSearchEnabled && _services.Hotkeys?.Current is null)
        {
            MessageBox.Show(this, "Los ajustes se han guardado, pero el atajo de teclado no se pudo registrar: puede que otra aplicación ya lo use. Elige otra combinación.",
                "PassDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    /// <summary>
    /// Tras cambiar la contraseña maestra, explica que las copias anteriores se siguen abriendo con la antigua y ofrece
    /// borrar los puntos de recuperación (la copia previa a Argon2id, aparte). Sin un «Sí» explícito no se borra nada.
    /// </summary>
    private void OnChangePassword(object sender, RoutedEventArgs e)
    {
        if (!PasswordDialog.ChangeMasterPassword(this, _services))
        {
            return;
        }

        // Todos los que hay ahora son anteriores al cambio.
        var store = new DpapiVaultStore(_services.Paths.VaultFile, _services.Paths.RecoveryDirectory);
        string[] points = store.ListRecoveryPoints();
        string[] preArgon2id = [.. points.Where(DpapiVaultStore.IsPreArgon2idPoint)];
        string[] older = [.. points.Except(preArgon2id)];
        bool Ask(string text, string caption) =>
            MessageBox.Show(this, text, caption, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

        string summary =
            "La contraseña maestra se ha cambiado y la bóveda se ha vuelto a cifrar con una clave nueva.\n\n" +
            "Las copias anteriores no cambian: se siguen abriendo con la contraseña anterior.\n" +
            (older.Length > 0 ? $"• {Formatting.Count(older.Length, "punto de recuperación", "puntos de recuperación")} de antes del cambio.\n" : "") +
            "• Las copias de seguridad (.pdbackup) que hayas exportado. PassDesk no sabe dónde las guardaste: bórralas tú " +
            "(también del USB o de la nube) y exporta una nueva.\n\n" +
            "Si has cambiado la contraseña porque otra persona la conoce, conviene borrarlas. Si no, puedes conservarlas: " +
            "para restaurarlas se pide la contraseña anterior.";
        var toDelete = new List<string>();
        if (older.Length == 0)
        {
            MessageBox.Show(this, summary, "Contraseña maestra cambiada", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (Ask(summary + "\n\n¿Borrar ahora los puntos de recuperación anteriores al cambio? No se puede deshacer.", "Contraseña maestra cambiada"))
        {
            toDelete.AddRange(older);
        }

        if (preArgon2id.Length > 0 && Ask(
                "Queda además la copia que se guardó antes de pasar la bóveda a Argon2id, por si había que volver atrás:\n" +
                string.Join("\n", preArgon2id.Select(Path.GetFileName)) + "\n\n" +
                "También se abre con la contraseña anterior y está protegida con PBKDF2, más fácil de atacar por fuerza bruta " +
                "que Argon2id.\n\n¿Borrarla? No se puede deshacer.",
                "Copia anterior a Argon2id"))
        {
            toDelete.AddRange(preArgon2id);
        }

        if (toDelete.Count == 0)
        {
            return;
        }

        var failed = store.DeleteRecoveryPoints(toDelete);
        string deleted = Formatting.Count(toDelete.Count - failed.Count, "punto de recuperación borrado.", "puntos de recuperación borrados.");
        if (failed.Count == 0)
        {
            MessageBox.Show(this, deleted, "PassDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(
            this,
            $"{deleted}\n\nNo se pudo borrar lo siguiente (puede que esté abierto en otro programa). Bórralo a mano en " +
            $"{store.RecoveryDirectory}:\n{string.Join("\n", failed.Select(Path.GetFileName))}",
            "PassDesk",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void OnExportBackup(object sender, RoutedEventArgs e)
    {
        // La copia no tiene la capa de Windows: con una contraseña maestra débil se podría adivinar sin límite de intentos.
        if (_services.Session.MasterPasswordMeetsPolicy == false)
        {
            var answer = MessageBox.Show(
                this,
                "Tu contraseña maestra es más débil de lo que PassDesk exige ahora (12 caracteres y fortaleza «Fuerte»).\n\n" +
                "La copia de seguridad no tiene la protección de Windows: si alguien la consigue (por ejemplo, en un USB o en la nube), " +
                "puede intentar adivinar la contraseña maestra sin límite de intentos.\n\n" +
                "Te recomendamos cambiarla antes («Cambiar contraseña maestra…»). ¿Exportar la copia igualmente?",
                "Contraseña maestra débil",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var dialog = new SaveFileDialog
        {
            Title = "Guardar copia de seguridad cifrada",
            Filter = "Copia de seguridad de PassDesk (*.pdbackup)|*.pdbackup",
            FileName = $"PassDesk-copia-{DateTime.Now:yyyy-MM-dd}{BackupFile.Extension}",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var documents = _services.Documents.CollectForBackup();
            BackupFile.WriteFile(dialog.FileName, _services.Session.CreateBackupEnvelope(), documents);
            AppLog.Info(LogEvent.BackupExported, new LogMetric("documents", documents.Count));
            ShowBackupStatus(
                $"Copia cifrada guardada en {dialog.FileName}" +
                (documents.Count > 0 ? $" (incluye {Formatting.Count(documents.Count, "documento", "documentos")})." : "."),
                error: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or VaultFormatException or VaultAccessDeniedException)
        {
            AppLog.Error(LogEvent.BackupFailed, ex);
            ShowBackupStatus("No se pudo guardar la copia en esa ubicación.", error: true);
        }
    }

    private void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Restaurar copia de seguridad de PassDesk",
            Filter = "Copia de seguridad de PassDesk (*.pdbackup)|*.pdbackup",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        BackupContents contents;
        try
        {
            contents = BackupFile.ReadFile(dialog.FileName);
        }
        catch (Exception ex) when (ex is VaultFormatException or IOException or UnauthorizedAccessException)
        {
            ShowBackupStatus("El archivo no es una copia de seguridad válida de PassDesk.", error: true);
            return;
        }

        Restore(contents, dialog.FileName, "Introduce la contraseña maestra que tenía PassDesk cuando se creó la copia.");
    }

    /// <summary>
    /// Puntos de recuperación: copias automáticas de la bóveda (igual de cifradas) guardadas antes de cada
    /// importación o restauración. Para deshacer una importación, elige el anterior y, en la vista previa,
    /// «Actualizar siempre con los datos importados».
    /// </summary>
    private void OnRestoreRecoveryPoint(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_services.Paths.RecoveryDirectory);
        var dialog = new OpenFileDialog
        {
            Title = "Restaurar un punto de recuperación de PassDesk",
            Filter = "Punto de recuperación de PassDesk (*.pdv)|*.pdv",
            InitialDirectory = _services.Paths.RecoveryDirectory,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        BackupContents contents;
        try
        {
            contents = new BackupContents(new DpapiVaultStore(dialog.FileName).ReadEnvelope(), new Dictionary<Guid, byte[]>());
        }
        catch (Exception ex) when (ex is VaultFormatException or VaultAccessDeniedException or IOException or UnauthorizedAccessException)
        {
            ShowBackupStatus("El archivo no es un punto de recuperación válido de PassDesk en esta cuenta de Windows.", error: true);
            return;
        }

        Restore(contents, dialog.FileName, "Introduce la contraseña maestra que tenía PassDesk cuando se guardó este punto (normalmente, la actual).");
    }

    private void Restore(BackupContents contents, string fileName, string passwordHint)
    {
        VaultDocument? document = null;
        bool opened = PasswordDialog.Prompt(
            this,
            _services,
            "Restaurar copia de seguridad",
            passwordHint,
            "Contraseña de la copia",
            async password =>
            {
                try
                {
                    document = await Task.Run(() => VaultSession.OpenBackupEnvelope(contents.Envelope, password.Span));
                    return null;
                }
                catch (InvalidMasterPasswordException)
                {
                    return "La contraseña no es correcta para esta copia.";
                }
                catch (VaultFormatException)
                {
                    return "La copia está dañada o no es válida.";
                }
                catch (CryptoUnavailableException)
                {
                    return CryptoUnavailableException.UserMessage;
                }
            });

        if (!opened || document is null)
        {
            return;
        }

        AppLog.Info(LogEvent.BackupRestoreOpened, new LogMetric("entries", document.Entries.Count), new LogMetric("documents", document.Documents.Count));
        ParseResult parsed;
        int restoredDocuments = 0;
        using (document)
        {
            if (document.Documents.Count > 0)
            {
                var answer = MessageBox.Show(
                    this,
                    $"La copia incluye {Formatting.Count(document.Documents.Count, "documento cifrado", "documentos cifrados")}. ¿Restaurarlos? (Los que ya tengas no se duplicarán.)",
                    "Restaurar documentos",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes);
                if (answer == MessageBoxResult.Yes)
                {
                    try
                    {
                        restoredDocuments = _services.Documents.RestoreFromBackup(document, contents.Documents);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        AppLog.Error(LogEvent.BackupFailed, ex);
                        ShowBackupStatus("No se pudieron restaurar los documentos.", error: true);
                    }
                }
            }

            parsed = ParseResult.FromEntries(document.Entries, "copia de seguridad cifrada de PassDesk");
        }

        if (restoredDocuments > 0)
        {
            ShowBackupStatus(Formatting.Count(restoredDocuments, "documento restaurado", "documentos restaurados") + ".", error: false);
        }

        if (parsed.Items.Count == 0)
        {
            parsed.Dispose();
            return;
        }

        var import = new ImportWindow(_services, parsed, EntrySources.Backup, Path.GetFileName(fileName)) { Owner = this };
        import.ShowDialog();
    }

    private void ShowBackupStatus(string message, bool error)
    {
        BackupStatus.Text = message;
        BackupStatus.Foreground = (System.Windows.Media.Brush)FindResource(error ? "Brush.Danger" : "Brush.Success");
        BackupStatus.Visibility = Visibility.Visible;
    }
}
