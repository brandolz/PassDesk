using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PassDesk.App.Infrastructure;
using PassDesk.App.ViewModels;
using PassDesk.Auth;
using PassDesk.Core.Model;
using PassDesk.Core.Services;
using PassDesk.Storage;

namespace PassDesk.App.Views;

public partial class MainWindow : Window, IDialogHost, ITrayHost
{
    public static readonly DependencyProperty IsSidebarExpandedProperty =
        DependencyProperty.Register(nameof(IsSidebarExpanded), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));

    private readonly AppServices _services;
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _toastTimer;
    private readonly TrayController _tray;
    private TrayIcon? _trayIcon;
    private QuickSearchWindow? _quickSearch;
    private bool _importOpen;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        _tray = new TrayController(this);
        _viewModel = new MainViewModel(services, this);
        DataContext = _viewModel;
        LockScreen.Initialize(services);
        WindowSecurity.Protect(this, services);
        SensitiveTextClipboard.Attach(NotesText, services.Clipboard, ClipboardItemKind.Note, CopiedToast("Nota copiada"), ClipboardFailedToast);
        SensitiveTextClipboard.Attach(DocumentText, services.Clipboard, ClipboardItemKind.DocumentText, CopiedToast("Texto copiado"), ClipboardFailedToast);

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
        _toastTimer.Tick += (_, _) => HideToast();

        SourceInitialized += (_, _) => services.AttachMainWindow(this);
        services.QuickSearchRequested += (_, _) => ShowQuickSearch();
        services.ExportDetected += (_, e) => OnExportDetected(e.Path);
        services.Session.StateChanged += (_, e) =>
        {
            if (e.NewState == VaultState.Unlocked)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    _viewModel.SetDetectedExport(_services.PendingExportPath);
                    if (IsActive)
                    {
                        TryOpenPendingExport();
                    }
                });
            }
        };
        Activated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, TryOpenPendingExport);
        Deactivated += (_, _) => _viewModel.HideSecrets();
        StateChanged += OnWindowStateChanged;
        SizeChanged += (_, _) => UpdateLayoutMode();
        PreviewKeyDown += OnWindowPreviewKeyDown;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedEntry) && _viewModel.SelectedEntry is not null)
            {
                DetailScroll.ScrollToTop();
                ((Storyboard)FindResource("FadeIn")).Begin(DetailPanel);
            }
        };
        Closing += (_, e) =>
        {
            if (_tray.OnClosing())
            {
                e.Cancel = true;
            }
        };
        Closed += (_, _) =>
        {
            _trayIcon?.Dispose();
            _quickSearch?.Close();
            _viewModel.Dispose();
        };

        _viewModel.Initialize();
    }

    public bool IsSidebarExpanded
    {
        get => (bool)GetValue(IsSidebarExpandedProperty);
        set => SetValue(IsSidebarExpandedProperty, value);
    }

    AppSettings ITrayHost.Settings => _services.Settings;

    bool ITrayHost.IsUnlocked => _viewModel.IsUnlocked;

    bool ITrayHost.IsTrayIconShown => _trayIcon?.IsShown == true;

    /// <summary>Muestra el icono de la bandeja. Lo llama la aplicación (la herramienta de capturas, no).</summary>
    internal void ShowTrayIcon() => _trayIcon ??= new TrayIcon(_tray);

    void ITrayHost.ShowMainWindow() => BringToFront();

    void ITrayHost.HideMainWindow()
    {
        _viewModel.HideSecrets();
        Hide();
    }

    void ITrayHost.ShowQuickSearch() => ShowQuickSearch();

    void ITrayHost.LockNow(LockReason reason) => _services.LockNow(reason);

    void ITrayHost.Shutdown() => Application.Current.Shutdown();

    public void BringToFront()
    {
        // Primero Show(): si se ocultó en la bandeja estando minimizada, WPF solo la restaura cuando ya es visible.
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Abre la cuenta elegida en la búsqueda rápida dentro de la ventana principal.</summary>
    internal void ShowEntry(Guid id)
    {
        BringToFront();
        _viewModel.SelectEntry(id);
    }

    public void ShowImport(string? filePath = null)
    {
        if (_importOpen)
        {
            return;
        }

        _importOpen = true;
        var window = new ImportWindow(_services, filePath) { Owner = this };
        try
        {
            window.ShowDialog();
        }
        finally
        {
            _importOpen = false;

            // La exportación detectada (o la que se acaba de importar) queda atendida: no se vuelve a avisar.
            foreach (string? handled in new[] { filePath, window.SourcePath })
            {
                if (handled is not null)
                {
                    _services.MarkExportHandled(handled);
                }
            }

            if (_services.PendingExportPath is null || !File.Exists(_services.PendingExportPath))
            {
                _services.DismissPendingExport();
                _viewModel.SetDetectedExport(null);
            }
        }
    }

    /// <summary>
    /// Se ha guardado una exportación de Apple en Escritorio, Descargas o Documentos. Si PassDesk está
    /// a la vista y desbloqueado, abre directamente la vista previa; si no, lo indica y espera.
    /// </summary>
    private void OnExportDetected(string path)
    {
        _viewModel.SetDetectedExport(path);
        if (IsActive && WindowState != WindowState.Minimized)
        {
            TryOpenPendingExport();
        }
        else
        {
            NativeMethods.FlashTaskbar(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        }
    }

    private void TryOpenPendingExport()
    {
        string? path = _services.PendingExportPath;
        if (path is null || _importOpen || !_viewModel.IsUnlocked || OwnedWindows.Count > 0 || WindowState == WindowState.Minimized)
        {
            return;
        }

        if (!File.Exists(path))
        {
            _services.DismissPendingExport();
            _viewModel.SetDetectedExport(null);
            return;
        }

        _viewModel.SetDetectedExport(path);
        ShowImport(path);
    }

    public void ShowGenerator()
    {
        var window = new GeneratorWindow(_services, pickMode: false) { Owner = this };
        window.ShowDialog();
    }

    public void ShowSettings()
    {
        var window = new SettingsWindow(_services) { Owner = this };
        window.ShowDialog();
    }

    public void ShowEditor(CredentialEntry? entry)
    {
        var window = new EditEntryWindow(_services, entry) { Owner = this };
        if (window.ShowDialog() == true && window.SavedEntryId is { } id)
        {
            _viewModel.SelectEntry(id);
            ShowToast(entry is null ? "Credencial añadida y cifrada" : "Cambios guardados", ToastKind.Success);
        }
    }

    public bool Confirm(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public IReadOnlyList<string> PickDocumentFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Añadir documentos cifrados",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "Documentos e imágenes|*.pdf;*.jpg;*.jpeg;*.png;*.heic;*.heif;*.gif;*.bmp;*.tif;*.tiff;*.webp;*.txt|Todos los archivos (*.*)|*.*",
            InitialDirectory = NativeMethods.GetKnownFolder(NativeMethods.FolderIdDownloads) ?? string.Empty,
        };
        return dialog.ShowDialog(this) == true ? dialog.FileNames : [];
    }

    public string? PickExportPath(string suggestedFileName)
    {
        string extension = Path.GetExtension(suggestedFileName);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar documento (la copia NO estará cifrada)",
            FileName = suggestedFileName,
            Filter = extension.Length > 0 ? $"Archivo {extension}|*{extension}|Todos los archivos (*.*)|*.*" : "Todos los archivos (*.*)|*.*",
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public string? PromptText(string title, string label, string initialValue)
    {
        var prompt = new TextPromptWindow(_services, title, label, initialValue) { Owner = this };
        return prompt.ShowDialog() == true ? prompt.Value : null;
    }

    private void OnDocumentsDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _viewModel.IsUnlocked && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDocumentsDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            _viewModel.AddDocuments(files.Where(File.Exists).ToList());
        }
    }

    public void ShowToast(string message, ToastKind kind = ToastKind.Info)
    {
        ToastText.Text = message;
        ToastGlyph.Text = kind switch
        {
            ToastKind.Success => "",
            ToastKind.Warning => "",
            ToastKind.Error => "",
            _ => "",
        };
        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private Action<TimeSpan> CopiedToast(string what) =>
        delay => ShowToast($"{what} · se borrará del portapapeles en {delay.TotalSeconds:0} s", ToastKind.Success);

    private void ClipboardFailedToast() => ShowToast("No se pudo acceder al portapapeles. Inténtalo de nuevo.", ToastKind.Error);

    private void HideToast()
    {
        _toastTimer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            if (Toast.Opacity == 0)
            {
                Toast.Visibility = Visibility.Collapsed;
            }
        };
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    private void ShowQuickSearch()
    {
        _quickSearch ??= new QuickSearchWindow(_services, this);
        _quickSearch.ShowCentered();
    }

    private void UpdateLayoutMode()
    {
        bool expanded = ActualWidth >= 980;
        if (expanded != IsSidebarExpanded)
        {
            IsSidebarExpanded = expanded;
            SidebarColumn.Width = new GridLength(expanded ? 236 : 64);
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            return;
        }

        _viewModel.HideSecrets();
        _tray.OnMinimized();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsUnlocked)
        {
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when _viewModel.HasSearch:
                _viewModel.SearchText = string.Empty;
                e.Handled = true;
                break;
            case Key.Down:
            case Key.Enter:
                if (_viewModel.VisibleEntries.Count > 0)
                {
                    _viewModel.SelectedEntry ??= _viewModel.VisibleEntries[0];
                    EntryList.UpdateLayout();
                    if (EntryList.ItemContainerGenerator.ContainerFromItem(_viewModel.SelectedEntry) is UIElement item)
                    {
                        item.Focus();
                    }
                    else
                    {
                        EntryList.Focus();
                    }

                    e.Handled = true;
                }

                break;
        }
    }
}
