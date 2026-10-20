using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PassDesk.App.Infrastructure;
using PassDesk.Auth;
using PassDesk.Core.Model;
using PassDesk.Import;
using PassDesk.Import.Sources;
using PassDesk.Storage;

namespace PassDesk.App.Views;

/// <summary>Fila de la vista previa: nunca contiene contraseñas, notas ni códigos.</summary>
public sealed record PreviewRow(string Status, string Title, string Site, string Username, string Password);

/// <summary>
/// Asistente de importación: explica el origen, permite elegir el archivo, muestra una vista previa
/// sin secretos con duplicados y conflictos, cifra y guarda, y ofrece borrar la exportación.
/// También se usa para restaurar copias de seguridad de PassDesk.
/// </summary>
public partial class ImportWindow : Window
{
    private readonly AppServices _services;
    private readonly string _sourceId;
    private readonly bool _preloaded;
    private ParseResult? _parsed;
    private ImportPreview? _preview;
    private string? _sourcePath;
    private FileFingerprint? _sourceFingerprint;
    private int _step;

    /// <param name="initialFile">Exportación ya localizada (por ejemplo, detectada en Descargas): se abre en la vista previa.</param>
    public ImportWindow(AppServices services, string? initialFile = null)
        : this(services, null, EntrySources.AppleExport, null)
    {
        if (initialFile is not null)
        {
            Loaded += async (_, _) => await LoadFileAsync(initialFile);
        }
    }

    public ImportWindow(AppServices services, ParseResult? preloaded, string sourceId, string? label)
    {
        InitializeComponent();
        _services = services;
        _sourceId = sourceId;
        _preloaded = preloaded is not null;
        WindowSecurity.Protect(this, services);
        AutoDetectInfo.Visibility = services.Settings.DetectNewExports ? Visibility.Visible : Visibility.Collapsed;
        OpenICloudButton.Visibility = AppleApps.IsICloudInstalled ? Visibility.Visible : Visibility.Collapsed;

        services.Session.StateChanged += OnSessionStateChanged;
        Closed += (_, _) =>
        {
            services.Session.StateChanged -= OnSessionStateChanged;
            DisposeParsed();
        };

        if (preloaded is not null)
        {
            Title = "Restaurar copia de seguridad";
            _parsed = preloaded;
            BuildPreview(label ?? "Copia de seguridad");
        }
        else
        {
            ShowStep(1);
        }
    }

    /// <summary>Archivo de origen elegido o detectado (null si no se ha cargado ninguno).</summary>
    public string? SourcePath => _sourcePath;

    private void OnOpenICloudPasswords(object sender, RoutedEventArgs e)
    {
        if (!AppleApps.TryOpenICloudPasswords())
        {
            ICloudHint.Text = "No se pudo abrir Contraseñas de iCloud. Ábrela desde el menú Inicio o usa la opción del iPhone.";
        }
    }

    private void OnSessionStateChanged(object? sender, VaultStateChangedEventArgs e)
    {
        // Si la bóveda se bloquea (inactividad, Win+L…), se cierra el asistente y se borra lo leído.
        if (e.NewState != VaultState.Unlocked)
        {
            Dispatcher.BeginInvoke(() =>
            {
                DisposeParsed();
                Close();
            });
        }
    }

    private async void OnChooseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecciona la exportación de Contraseñas de Apple",
            Filter = "Exportación de contraseñas (*.csv;*.zip)|*.csv;*.zip|Todos los archivos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await LoadFileAsync(dialog.FileName);
    }

    /// <summary>Admite arrastrar el CSV o ZIP exportado sobre la ventana (solo en el primer paso).</summary>
    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _step == 1 && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (_step == 1 && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files)
        {
            await LoadFileAsync(files[0]);
        }
    }

    /// <summary>Lee el archivo elegido y muestra la vista previa (también lo usa la herramienta de capturas).</summary>
    internal async Task LoadFileAsync(string path)
    {
        _sourcePath = path;
        _sourceFingerprint = null;
        SourceErrorBar.Visibility = Visibility.Collapsed;
        ReadingProgress.Visibility = Visibility.Visible;
        ChooseFileButton.IsEnabled = false;
        try
        {
            var parsed = await _services.Sources.Get("apple-export").FetchAsync(new CredentialSourceRequest(_sourcePath), CancellationToken.None);
            DisposeParsed();
            if (!IsLoaded || _services.Session.State != VaultState.Unlocked)
            {
                parsed.Dispose();
                return;
            }

            _parsed = parsed;
            _sourceFingerprint = parsed.SourceFingerprint;
            BuildPreview(Path.GetFileName(_sourcePath));
        }
        catch (ImportFileException ex)
        {
            // Los mensajes de ImportFileException son genéricos y no contienen datos del archivo.
            ShowSourceError(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowSourceError("No se pudo leer el archivo seleccionado.");
        }
        finally
        {
            ReadingProgress.Visibility = Visibility.Collapsed;
            ChooseFileButton.IsEnabled = true;
        }
    }

    private void ShowSourceError(string message)
    {
        SourceError.Text = message;
        SourceErrorBar.Visibility = Visibility.Visible;
    }

    private void BuildPreview(string label)
    {
        if (_parsed is null)
        {
            return;
        }

        _preview = ImportAnalyzer.Analyze(_services.Session.Entries, _parsed);
        PreviewSource.Text = $"{label} · {_parsed.FormatName}";
        NewChip.Text = Formatting.Count(_preview.NewCount, "nueva", "nuevas");
        DuplicateChip.Text = $"{Formatting.Count(_preview.DuplicateCount, "ya existente", "ya existentes")} (se omiten)";
        ConflictChip.Text = Formatting.Count(_preview.ConflictCount, "con cambios", "con cambios");
        InvalidChip.Text = Formatting.Count(_preview.InvalidCount, "fila no válida", "filas no válidas");

        PreviewList.ItemsSource = _preview.Items
            .Select(i => new PreviewRow(StatusText(i.Status), i.Item.Title, i.Item.DisplayHost, i.Item.Username, "••••••••"))
            .ToList();
        ConflictOptions.Visibility = _preview.ConflictCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        var notes = new List<string>();
        if (_preview.InvalidCount > 0)
        {
            var lines = _parsed.Issues.Take(12).Select(i => i.Line.ToString(Formatting.Spanish));
            string more = _parsed.Issues.Count > 12 ? "…" : string.Empty;
            notes.Add($"{Formatting.Count(_preview.InvalidCount, "fila no se importará", "filas no se importarán")} (sin contraseña, sin identificación o con formato incorrecto). Líneas: {string.Join(", ", lines)}{more}.");
        }

        int codes = _parsed.Items.Count(i => !i.OtpAuth.IsEmpty);
        if (codes > 0)
        {
            notes.Add($"Incluye {Formatting.Count(codes, "código de verificación", "códigos de verificación")} (se guardan cifrados).");
        }

        if (_sourcePath is not null)
        {
            notes.AddRange(SecureFileDeleter.GetLocationWarnings(_sourcePath));
        }

        if (_parsed.Items.Count == 0)
        {
            notes.Add("El archivo no contiene credenciales que importar.");
        }

        WarningsText.Text = string.Join(Environment.NewLine, notes.Select(n => "• " + n));
        WarningsBar.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool offerDelete = !_preloaded && _sourcePath is not null;
        DeleteAfterImport.Visibility = offerDelete ? Visibility.Visible : Visibility.Collapsed;
        DeleteAfterImport.IsChecked = _services.Settings.DeleteExportAfterImport;
        UpdatePrimaryButton();
        ShowStep(2);
    }

    private ConflictResolution SelectedResolution =>
        ConflictUpdate.IsChecked == true ? ConflictResolution.UpdateExisting
        : ConflictKeepBoth.IsChecked == true ? ConflictResolution.KeepBoth
        : ConflictSkip.IsChecked == true ? ConflictResolution.Skip
        : ConflictResolution.UpdateUnlessChangedLocally;

    private void UpdatePrimaryButton()
    {
        if (_preview is null)
        {
            return;
        }

        // Cálculo exacto de lo que hará la opción elegida, sin cambiar nada todavía.
        var plan = ImportApplier.Simulate(_services.Session.Entries, _preview, SelectedResolution);
        int toImport = plan.Added + plan.Updated;
        PrimaryButton.Content = toImport == 0 ? "Nada que importar" : $"Importar {Formatting.Count(toImport, "credencial", "credenciales")}";
        PrimaryButton.IsEnabled = toImport > 0;
        if (_preview.ConflictCount > 0)
        {
            int kept = _preview.ConflictCount - (plan.Updated + plan.Added - _preview.NewCount);
            ConflictPlan.Text = $"Con esta opción, de las {Formatting.Count(_preview.ConflictCount, "cuenta con cambios", "cuentas con cambios")}: " +
                $"{plan.Updated:N0} se actualizan · {plan.Added - _preview.NewCount:N0} se añaden como otra versión · {kept:N0} se quedan como están.";
        }
    }

    private static string StatusText(ImportItemStatus status) => status switch
    {
        ImportItemStatus.New => "Nueva",
        ImportItemStatus.Duplicate => "Ya existe (se omite)",
        ImportItemStatus.Conflict => "Con cambios",
        _ => "Repetida en el archivo",
    };

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (_step == 2)
        {
            Apply();
        }
        else if (_step == 3)
        {
            DialogResult = true;
        }
    }

    private void Apply()
    {
        if (_preview is null)
        {
            return;
        }

        var resolution = SelectedResolution;
        var now = DateTimeOffset.UtcNow;
        var preview = _preview;

        // Punto de recuperación: copia cifrada de la bóveda tal como está, para poder deshacer la importación.
        try
        {
            _services.Session.CreateRecoveryPoint(_preloaded ? "restauracion" : "importacion");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, "No se pudo guardar el punto de recuperación previo. No se ha importado nada.", "PassDesk", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ImportOutcome outcome;
        try
        {
            outcome = _services.Session.Mutate(document =>
            {
                var result = ImportApplier.Apply(document.Entries, preview, resolution, now, _sourceId);
                document.LastImport = new ImportRecord(now, _sourceId, result.Added, result.Updated, result.Skipped);
                return result;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            // La sesión deshace el cambio en memoria si no se pudo guardar: esto es literalmente cierto.
            MessageBox.Show(this, "No se pudo guardar la bóveda cifrada. No se ha importado nada.", "PassDesk", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Los datos leídos del archivo ya están cifrados en la bóveda: se borran de memoria.
        DisposeParsed();

        ResultSummary.Text =
            $"Se añadieron {outcome.Added:N0} · se actualizaron {outcome.Updated:N0} · se omitieron {outcome.Skipped:N0}" +
            (outcome.Invalid > 0 ? " · " + Formatting.Count(outcome.Invalid, "fila no válida", "filas no válidas") : string.Empty) + ".";

        bool deleteChoiceShown = DeleteAfterImport.Visibility == Visibility.Visible;
        bool autoDelete = deleteChoiceShown && DeleteAfterImport.IsChecked == true;
        if (deleteChoiceShown && _services.Settings.DeleteExportAfterImport != autoDelete)
        {
            RememberDeleteChoice(autoDelete);
        }

        bool fileExists = !_preloaded && _sourcePath is not null && File.Exists(_sourcePath);
        DeleteSection.Visibility = fileExists ? Visibility.Visible : Visibility.Collapsed;
        if (fileExists)
        {
            DeletePath.Text = _sourcePath;
            if (autoDelete && outcome.AllDataImported)
            {
                // Solo si el archivo sigue siendo exactamente el que se leyó (tamaño y SHA-256).
                ShowDeleteResult(SecureFileDeleter.Delete(_sourcePath!, _sourceFingerprint));
            }
            else
            {
                var notes = new List<string>();
                if (autoDelete)
                {
                    notes.Add("No se ha borrado automáticamente porque parte del archivo no se ha guardado en PassDesk " +
                        "(filas no válidas o cuentas con cambios que decidiste no aplicar). Revísalo antes de eliminarlo.");
                }

                notes.AddRange(SecureFileDeleter.GetLocationWarnings(_sourcePath!));
                DeleteWarnings.Text = Bullets(notes.Concat(Reminders));
            }
        }

        ShowStep(3);
    }

    private static readonly string[] Reminders =
    [
        "Si la exportación se creó en el iPhone, bórrala también de Archivos › Descargas y de «Eliminados recientemente».",
        "Si la pasaste por correo, mensajería o la nube, elimina también esas copias.",
    ];

    private static string Bullets(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines.Distinct().Select(w => "• " + w));

    private void RememberDeleteChoice(bool delete)
    {
        try
        {
            var settings = _services.Settings.Clone();
            settings.DeleteExportAfterImport = delete;
            _services.UpdateSettings(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferencia no esencial: si no se puede guardar, se pregunta de nuevo la próxima vez.
        }
    }

    private void OnDeleteSource(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Se sobrescribirá con datos aleatorios y se eliminará definitivamente (sin pasar por la Papelera):\n\n{_sourcePath}\n\n¿Continuar?",
            "Eliminar archivo exportado",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        ShowDeleteResult(SecureFileDeleter.Delete(_sourcePath, _sourceFingerprint));
    }

    private void ShowDeleteResult(SecureDeleteResult result)
    {
        DeleteResult.Visibility = Visibility.Visible;
        DeleteResult.Text = result.Deleted ? "✓ Archivo sobrescrito con datos aleatorios y eliminado." : result.Error ?? "No se pudo eliminar el archivo.";
        DeleteResult.Foreground = (System.Windows.Media.Brush)FindResource(result.Deleted ? "Brush.Success" : "Brush.Danger");
        DeleteButton.Visibility = result.Deleted ? Visibility.Collapsed : Visibility.Visible;
        if (result.Deleted)
        {
            DeleteGlyph.Text = "";
            DeleteGlyph.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Success");
            DeleteTitle.Text = "Archivo exportado eliminado";
            DeleteDescription.Text = "PassDesk ha borrado de este PC el archivo con tus contraseñas sin cifrar (sin pasar por la Papelera).";
        }

        DeleteWarnings.Text = Bullets(result.Warnings.Concat(Reminders));
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        DisposeParsed();
        ShowStep(1);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DisposeParsed();
        if (_step == 3)
        {
            DialogResult = true;
        }
        else
        {
            Close();
        }
    }

    private void DisposeParsed()
    {
        PreviewList.ItemsSource = null;
        _preview = null;
        _parsed?.Dispose();
        _parsed = null;
    }

    private void ShowStep(int step)
    {
        _step = step;
        SourceStep.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        PreviewStep.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        ResultStep.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

        Border[] chips = [Step1Chip, Step2Chip, Step3Chip];
        for (int i = 0; i < chips.Length; i++)
        {
            chips[i].Background = (System.Windows.Media.Brush)FindResource(i + 1 == step ? "Brush.AccentSubtle" : "Brush.Chip");
        }

        BackButton.Visibility = step == 2 && !_preloaded ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = step == 3 ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Visibility = step == 1 ? Visibility.Collapsed : Visibility.Visible;
        if (step == 3)
        {
            PrimaryButton.Content = "Finalizar";
            PrimaryButton.IsEnabled = true;
        }

        FooterNote.Text = step switch
        {
            1 => "Nada se importa hasta que confirmes la vista previa.",
            2 => "Puedes cancelar sin que se guarde nada.",
            _ => string.Empty,
        };
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        ConflictSmart.Checked += (_, _) => UpdatePrimaryButton();
        ConflictUpdate.Checked += (_, _) => UpdatePrimaryButton();
        ConflictKeepBoth.Checked += (_, _) => UpdatePrimaryButton();
        ConflictSkip.Checked += (_, _) => UpdatePrimaryButton();
    }
}
