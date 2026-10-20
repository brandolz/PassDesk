using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PassDesk.App.Infrastructure;
using PassDesk.Auth;
using PassDesk.Core.Model;
using PassDesk.Core.Search;
using PassDesk.Core.Services;
using PassDesk.Core.Text;
using PassDesk.Import.Sources;

namespace PassDesk.App.ViewModels;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Operaciones de interfaz que el modelo de vista delega en la ventana.</summary>
public interface IDialogHost
{
    /// <summary>Abre el asistente de importación; con <paramref name="filePath"/>, directamente en la vista previa.</summary>
    void ShowImport(string? filePath = null);

    void ShowGenerator();

    void ShowSettings();

    void ShowEditor(CredentialEntry? entry);

    bool Confirm(string message, string title);

    void ShowToast(string message, ToastKind kind = ToastKind.Info);

    /// <summary>Elige archivos para añadir como documentos cifrados (vacío si se cancela).</summary>
    IReadOnlyList<string> PickDocumentFiles();

    /// <summary>Elige dónde guardar una copia sin cifrar de un documento (null si se cancela).</summary>
    string? PickExportPath(string suggestedFileName);

    /// <summary>Pide un texto corto (null si se cancela).</summary>
    string? PromptText(string title, string label, string initialValue);
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly IDialogHost _dialogs;
    private readonly Dictionary<Guid, EntryItemViewModel> _items = [];
    private readonly DispatcherTimer _ticker;
    private List<CredentialEntry> _entries = [];
    private IReadOnlyList<EntryItemViewModel> _visibleEntries = [];
    private string _searchText = string.Empty;
    private CategoryItemViewModel _selectedCategory;
    private SortOption _selectedSort;
    private EntryItemViewModel? _selectedEntry;
    private VaultState _state;
    private string? _detectedExport;
    private bool _reminderDismissed;
    private readonly Dictionary<Guid, DocumentItemViewModel> _documentItems = [];
    private List<SecureDocument> _documents = [];
    private IReadOnlyList<DocumentItemViewModel> _visibleDocuments = [];
    private DocumentItemViewModel? _selectedDocument;

    public MainViewModel(AppServices services, IDialogHost dialogs)
    {
        _services = services;
        _dialogs = dialogs;
        DocumentDetail = new DocumentDetailViewModel(services.Documents);
        Categories =
        [
            new CategoryItemViewModel(null),
            new CategoryItemViewModel(Category.Social),
            new CategoryItemViewModel(Category.Email),
            new CategoryItemViewModel(Category.Gaming),
            new CategoryItemViewModel(Category.Banking),
            new CategoryItemViewModel(Category.Shopping),
            new CategoryItemViewModel(Category.Other),
            new CategoryItemViewModel(null, isDocuments: true),
        ];
        _selectedCategory = Categories[0];
        SortOptions =
        [
            new SortOption(SortMode.NameAscending, "Nombre (A–Z)"),
            new SortOption(SortMode.NameDescending, "Nombre (Z–A)"),
            new SortOption(SortMode.ModifiedNewest, "Modificación más reciente"),
            new SortOption(SortMode.ModifiedOldest, "Modificación más antigua"),
        ];
        _selectedSort = SortOptions.FirstOrDefault(o => o.Mode == services.Settings.SortMode) ?? SortOptions[0];

        LockCommand = new RelayCommand(() => _services.LockNow(LockReason.Manual), () => IsUnlocked);
        ImportCommand = new RelayCommand(() => _dialogs.ShowImport(), () => IsUnlocked);
        GeneratorCommand = new RelayCommand(() => _dialogs.ShowGenerator());
        SettingsCommand = new RelayCommand(() => _dialogs.ShowSettings(), () => IsUnlocked);
        AddCommand = new RelayCommand(
            () =>
            {
                if (IsDocumentsView)
                {
                    AddDocuments(null);
                }
                else
                {
                    _dialogs.ShowEditor(null);
                }
            },
            () => IsUnlocked);
        AddDocumentsCommand = new RelayCommand(() => AddDocuments(null), () => IsUnlocked);
        ExportDocumentCommand = new RelayCommand(ExportSelectedDocument, () => IsUnlocked && DocumentDetail.HasDocument);
        RenameDocumentCommand = new RelayCommand(RenameSelectedDocument, () => IsUnlocked && DocumentDetail.HasDocument);
        DeleteDocumentCommand = new RelayCommand(DeleteSelectedDocument, () => IsUnlocked && DocumentDetail.HasDocument);
        EditCommand = new RelayCommand(() => _dialogs.ShowEditor(Detail.Entry), () => IsUnlocked && Detail.HasEntry);
        DeleteCommand = new RelayCommand(DeleteSelected, () => IsUnlocked && Detail.HasEntry);
        CopyUsernameCommand = new RelayCommand(() => Copy(ClipboardItemKind.Username), () => IsUnlocked && Detail.HasUsername);
        CopyPasswordCommand = new RelayCommand(() => Copy(ClipboardItemKind.Password), () => IsUnlocked && Detail.HasPassword);
        CopyCodeCommand = new RelayCommand(() => Copy(ClipboardItemKind.VerificationCode), () => IsUnlocked && Detail.HasCode);
        OpenWebsiteCommand = new RelayCommand(OpenWebsite, () => IsUnlocked && Detail.HasUrl);
        ToggleRevealCommand = new RelayCommand(() => Detail.ToggleReveal(), () => IsUnlocked && Detail.HasPassword);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        ImportDetectedCommand = new RelayCommand(() => _dialogs.ShowImport(_detectedExport), () => IsUnlocked && _detectedExport is not null);
        DismissDetectedCommand = new RelayCommand(() =>
        {
            _services.DismissPendingExport();
            SetDetectedExport(null);
        });
        DismissReminderCommand = new RelayCommand(() =>
        {
            _reminderDismissed = true;
            OnPropertyChanged(nameof(ShowUpdateReminder));
        });

        _state = services.Session.State;
        services.Session.StateChanged += OnStateChanged;
        services.Session.DataChanged += OnDataChanged;
        _ticker = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Detail.Tick(), Application.Current.Dispatcher);
        _ticker.Start();
    }

    public IReadOnlyList<CategoryItemViewModel> Categories { get; }

    public IReadOnlyList<SortOption> SortOptions { get; }

    public EntryDetailViewModel Detail { get; } = new();

    public DocumentDetailViewModel DocumentDetail { get; }

    public ICommand AddDocumentsCommand { get; }

    public ICommand ExportDocumentCommand { get; }

    public ICommand RenameDocumentCommand { get; }

    public ICommand DeleteDocumentCommand { get; }

    /// <summary>La barra lateral tiene seleccionada la entrada «Documentos».</summary>
    public bool IsDocumentsView => SelectedCategory.IsDocuments;

    public IReadOnlyList<DocumentItemViewModel> VisibleDocuments
    {
        get => _visibleDocuments;
        private set => Set(ref _visibleDocuments, value);
    }

    public DocumentItemViewModel? SelectedDocument
    {
        get => _selectedDocument;
        set
        {
            if (Set(ref _selectedDocument, value))
            {
                _ = DocumentDetail.ShowAsync(value?.Document);
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsDocumentsEmpty => IsUnlocked && IsDocumentsView && _documents.Count == 0;

    public bool HasNoDocumentResults => IsUnlocked && IsDocumentsView && _documents.Count > 0 && VisibleDocuments.Count == 0;

    public string SearchPlaceholder => IsDocumentsView ? "Buscar documento  (Ctrl+F)" : "Buscar servicio, web o usuario  (Ctrl+F)";

    public string AddToolTip => IsDocumentsView ? "Añadir documentos cifrados (Ctrl+N)" : "Nueva credencial (Ctrl+N)";

    public ICommand LockCommand { get; }

    public ICommand ImportCommand { get; }

    public ICommand GeneratorCommand { get; }

    public ICommand SettingsCommand { get; }

    public ICommand AddCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand CopyUsernameCommand { get; }

    public ICommand CopyPasswordCommand { get; }

    public ICommand CopyCodeCommand { get; }

    public ICommand OpenWebsiteCommand { get; }

    public ICommand ToggleRevealCommand { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand ImportDetectedCommand { get; }

    public ICommand DismissDetectedCommand { get; }

    public ICommand DismissReminderCommand { get; }

    /// <summary>Aviso de exportación de Apple detectada en Escritorio, Descargas o Documentos.</summary>
    public bool HasDetectedExport => IsUnlocked && _detectedExport is not null;

    public string DetectedExportText => _detectedExport is null
        ? string.Empty
        : $"Nueva exportación de contraseñas detectada: {Path.GetFileName(_detectedExport)}";

    /// <summary>Recordatorio cuando hace 30 días o más de la última actualización desde Apple.</summary>
    public bool ShowUpdateReminder =>
        IsUnlocked && !_reminderDismissed && _detectedExport is null && _services.Settings.UpdateReminder
        && _services.Session.LastImport is { } last && DateTimeOffset.UtcNow - last.TimestampUtc >= TimeSpan.FromDays(30);

    public string UpdateReminderText => _services.Session.LastImport is { } last
        ? $"Hace {(int)(DateTimeOffset.UtcNow - last.TimestampUtc).TotalDays} días que no actualizas desde Apple: puede faltar alguna contraseña nueva."
        : string.Empty;

    public void SetDetectedExport(string? path)
    {
        _detectedExport = path;
        OnPropertiesChanged(nameof(HasDetectedExport), nameof(DetectedExportText), nameof(ShowUpdateReminder));
        CommandManager.InvalidateRequerySuggested();
    }

    public VaultState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertiesChanged(nameof(IsUnlocked), nameof(IsLocked), nameof(HasDetectedExport), nameof(ShowUpdateReminder));
            }
        }
    }

    public bool IsUnlocked => State == VaultState.Unlocked;

    public bool IsLocked => State != VaultState.Unlocked;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasSearch));
                ApplyFilter();
            }
        }
    }

    public bool HasSearch => _searchText.Length > 0;

    public CategoryItemViewModel SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is not null && Set(ref _selectedCategory, value))
            {
                // Al cambiar entre credenciales y documentos se descarta la selección de la otra vista,
                // para que los atajos (copiar, etc.) nunca actúen sobre algo que no se ve.
                if (value.IsDocuments)
                {
                    SelectedEntry = null;
                }
                else
                {
                    SelectedDocument = null;
                }

                OnPropertiesChanged(nameof(IsDocumentsView), nameof(SearchPlaceholder), nameof(AddToolTip));
                ApplyFilter();
            }
        }
    }

    public SortOption SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (value is not null && Set(ref _selectedSort, value))
            {
                var settings = _services.Settings.Clone();
                settings.SortMode = value.Mode;
                _services.UpdateSettings(settings);
                ApplyFilter();
            }
        }
    }

    public IReadOnlyList<EntryItemViewModel> VisibleEntries
    {
        get => _visibleEntries;
        private set => Set(ref _visibleEntries, value);
    }

    public EntryItemViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (Set(ref _selectedEntry, value))
            {
                Detail.Show(value?.Entry);
            }
        }
    }

    public string ListHeader => SelectedCategory.Name;

    public string CountText =>
        IsDocumentsView
            ? (HasSearch
                ? $"{Formatting.Count(VisibleDocuments.Count, "resultado", "resultados")} de {_documents.Count:N0}"
                : Formatting.Count(_documents.Count, "documento cifrado", "documentos cifrados"))
            : HasSearch || SelectedCategory.Category is not null
                ? $"{Formatting.Count(VisibleEntries.Count, "resultado", "resultados")} de {_entries.Count:N0}"
                : Formatting.Count(_entries.Count, "credencial", "credenciales");

    public string TotalText => Formatting.Count(_entries.Count, "credencial guardada", "credenciales guardadas");

    public string LastImportText => _services.Session.LastImport is { } last
        ? $"Última importación: {Formatting.Date(last.TimestampUtc)}"
        : "Sin importaciones todavía";

    public string SyncStatusText =>
        _services.Sources.FindAutomaticSyncSource() is null
            ? "Sincronización automática no disponible · importación manual"
            : "Sincronización automática activa";

    public string SyncExplanation => ICloudDirectSyncSource.Explanation;

    public bool IsVaultEmpty => IsUnlocked && !IsDocumentsView && _entries.Count == 0;

    public bool HasNoResults => IsUnlocked && !IsDocumentsView && _entries.Count > 0 && VisibleEntries.Count == 0;

    public void Dispose()
    {
        _ticker.Stop();
        _services.Session.StateChanged -= OnStateChanged;
        _services.Session.DataChanged -= OnDataChanged;
        Detail.Clear();
        DocumentDetail.Clear();
    }

    /// <summary>Cifra y añade documentos; después ofrece borrar los originales sin cifrar.</summary>
    public void AddDocuments(IReadOnlyList<string>? paths)
    {
        if (!IsUnlocked)
        {
            return;
        }

        paths ??= _dialogs.PickDocumentFiles();
        if (paths.Count == 0)
        {
            return;
        }

        DocumentAddResult result;
        try
        {
            result = _services.Documents.AddFiles(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _dialogs.ShowToast("No se pudieron guardar los documentos en la bóveda.", ToastKind.Error);
            return;
        }

        var notes = new List<string>();
        if (result.Duplicates > 0)
        {
            notes.Add(Formatting.Count(result.Duplicates, "ya estaba guardado", "ya estaban guardados"));
        }

        if (result.TooLarge > 0)
        {
            notes.Add(Formatting.Count(result.TooLarge, "supera 50 MB", "superan 50 MB"));
        }

        if (result.Failed > 0)
        {
            notes.Add(Formatting.Count(result.Failed, "no se pudo leer", "no se pudieron leer"));
        }

        string summary = Formatting.Count(result.Added, "documento añadido y cifrado", "documentos añadidos y cifrados")
            + (notes.Count > 0 ? " · " + string.Join(" · ", notes) : string.Empty);
        _dialogs.ShowToast(summary, result.Added > 0 ? ToastKind.Success : ToastKind.Warning);

        if (result.AddedPaths.Count > 0)
        {
            SelectedCategory = Categories.First(c => c.IsDocuments);
            bool deleteOriginals = _dialogs.Confirm(
                $"{summary}.\n\nLos archivos originales siguen en el PC sin cifrar. ¿Eliminarlos ahora de forma segura?\n\n" +
                "Solo se borrarán de este PC; los documentos quedan guardados (cifrados) en PassDesk.",
                "Eliminar originales");
            if (deleteOriginals)
            {
                int deleted = result.AddedPaths.Count(path => Import.SecureFileDeleter.Delete(path).Deleted);
                _dialogs.ShowToast(
                    Formatting.Count(deleted, "original eliminado de forma segura", "originales eliminados de forma segura"),
                    deleted == result.AddedPaths.Count ? ToastKind.Success : ToastKind.Warning);
            }
        }
    }

    private void ExportSelectedDocument()
    {
        if (DocumentDetail.Document is not { } document)
        {
            return;
        }

        string? destination = _dialogs.PickExportPath(document.FileName);
        if (destination is null)
        {
            return;
        }

        try
        {
            _services.Documents.Export(document.Id, destination);
            _dialogs.ShowToast("Documento exportado SIN cifrar. Bórralo cuando ya no lo necesites.", ToastKind.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Crypto.VaultFormatException or KeyNotFoundException)
        {
            _dialogs.ShowToast("No se pudo exportar el documento.", ToastKind.Error);
        }
    }

    private void RenameSelectedDocument()
    {
        if (DocumentDetail.Document is not { } document)
        {
            return;
        }

        string? title = _dialogs.PromptText("Cambiar nombre", "Nombre del documento", document.Title);
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        try
        {
            _services.Documents.Rename(document.Id, title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowToast("No se pudo guardar el cambio.", ToastKind.Error);
        }
    }

    private void DeleteSelectedDocument()
    {
        if (DocumentDetail.Document is not { } document)
        {
            return;
        }

        if (!_dialogs.Confirm(
                $"¿Eliminar «{document.Title}» de PassDesk?\n\nSe borrará el documento cifrado de este equipo. Si no tienes otra copia, no podrás recuperarlo.",
                "Eliminar documento"))
        {
            return;
        }

        try
        {
            _services.Documents.Delete(document.Id);
            _dialogs.ShowToast("Documento eliminado", ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowToast("No se pudo eliminar el documento.", ToastKind.Error);
        }
    }

    /// <summary>Vuelve a ocultar la contraseña revelada (por ejemplo, al perder el foco la ventana).</summary>
    public void HideSecrets() => Detail.Hide();

    public void SelectEntry(Guid id)
    {
        SearchText = string.Empty;
        SelectedCategory = Categories[0];
        SelectedEntry = VisibleEntries.FirstOrDefault(i => i.Id == id);
    }

    public void Initialize()
    {
        if (IsUnlocked)
        {
            Reload();
        }
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private void OnStateChanged(object? sender, VaultStateChangedEventArgs e) => Dispatch(() =>
    {
        State = e.NewState;
        if (e.NewState == VaultState.Unlocked)
        {
            Reload();
        }
        else
        {
            ClearData();
        }

        CommandManager.InvalidateRequerySuggested();
    });

    private void OnDataChanged(object? sender, EventArgs e) => Dispatch(Reload);

    private void Reload()
    {
        if (_services.Session.State != VaultState.Unlocked)
        {
            return;
        }

        _entries = [.. _services.Session.Entries];
        var alive = new HashSet<Guid>(_entries.Select(e => e.Id));
        foreach (var stale in _items.Keys.Where(id => !alive.Contains(id)).ToList())
        {
            _items.Remove(stale);
        }

        foreach (var entry in _entries)
        {
            if (_items.TryGetValue(entry.Id, out var item) && ReferenceEquals(item.Entry, entry))
            {
                item.Refresh();
            }
            else
            {
                _items[entry.Id] = new EntryItemViewModel(entry);
            }
        }

        _documents = [.. _services.Session.Documents];
        var aliveDocuments = new HashSet<Guid>(_documents.Select(d => d.Id));
        foreach (var stale in _documentItems.Keys.Where(id => !aliveDocuments.Contains(id)).ToList())
        {
            _documentItems.Remove(stale);
        }

        foreach (var document in _documents)
        {
            if (_documentItems.TryGetValue(document.Id, out var item) && ReferenceEquals(item.Document, document))
            {
                item.Refresh();
            }
            else
            {
                _documentItems[document.Id] = new DocumentItemViewModel(document);
            }
        }

        foreach (var category in Categories)
        {
            category.Count = category.IsDocuments
                ? _documents.Count
                : category.Category is { } c ? _entries.Count(e => e.Category == c) : _entries.Count;
        }

        ApplyFilter();
        if (Detail.Entry is { } shown && alive.Contains(shown.Id))
        {
            Detail.Show(shown);
        }

        if (DocumentDetail.Document is { } shownDocument)
        {
            if (aliveDocuments.Contains(shownDocument.Id))
            {
                _ = DocumentDetail.ShowAsync(shownDocument);
            }
            else
            {
                SelectedDocument = null;
            }
        }

        OnPropertiesChanged(nameof(TotalText), nameof(LastImportText), nameof(IsVaultEmpty), nameof(ShowUpdateReminder), nameof(UpdateReminderText), nameof(IsDocumentsEmpty));
    }

    private void ClearData()
    {
        Detail.Clear();
        DocumentDetail.Clear();
        _selectedEntry = null;
        _selectedDocument = null;
        _entries = [];
        _items.Clear();
        _documents = [];
        _documentItems.Clear();
        _searchText = string.Empty;
        VisibleEntries = [];
        VisibleDocuments = [];
        foreach (var category in Categories)
        {
            category.Count = 0;
        }

        OnPropertiesChanged(nameof(SelectedEntry), nameof(SelectedDocument), nameof(SearchText), nameof(HasSearch), nameof(CountText), nameof(TotalText),
            nameof(IsVaultEmpty), nameof(HasNoResults), nameof(IsDocumentsEmpty), nameof(HasNoDocumentResults));
    }

    private void ApplyFilter()
    {
        if (IsDocumentsView)
        {
            ApplyDocumentFilter();
            return;
        }

        Guid? previous = SelectedEntry?.Id;
        var results = CredentialSearch.Apply(_entries, SearchText, SelectedCategory.Category, SelectedSort.Mode);
        VisibleEntries = results.Where(e => _items.ContainsKey(e.Id)).Select(e => _items[e.Id]).ToList();

        var keep = previous is { } id ? VisibleEntries.FirstOrDefault(i => i.Id == id) : null;
        if (keep is null && HasSearch && VisibleEntries.Count > 0)
        {
            keep = VisibleEntries[0];
        }

        SelectedEntry = keep;
        OnPropertiesChanged(nameof(CountText), nameof(HasNoResults), nameof(ListHeader), nameof(IsVaultEmpty), nameof(IsDocumentsEmpty), nameof(HasNoDocumentResults));
    }

    private void ApplyDocumentFilter()
    {
        Guid? previous = SelectedDocument?.Id;
        var results = DocumentSearch.Apply(_documents, SearchText, SelectedSort.Mode);
        VisibleDocuments = results.Where(d => _documentItems.ContainsKey(d.Id)).Select(d => _documentItems[d.Id]).ToList();

        var keep = previous is { } id ? VisibleDocuments.FirstOrDefault(i => i.Id == id) : null;
        if (keep is null && HasSearch && VisibleDocuments.Count > 0)
        {
            keep = VisibleDocuments[0];
        }

        SelectedDocument = keep;
        OnPropertiesChanged(nameof(CountText), nameof(ListHeader), nameof(IsDocumentsEmpty), nameof(HasNoDocumentResults), nameof(HasNoResults), nameof(IsVaultEmpty));
    }

    private void Copy(ClipboardItemKind kind)
    {
        var entry = Detail.Entry;
        if (entry is null)
        {
            return;
        }

        string text = kind switch
        {
            ClipboardItemKind.Username => entry.Username,
            ClipboardItemKind.VerificationCode => Detail.CurrentCode,
            _ => entry.Password.Reveal(),
        };

        if (text.Length == 0)
        {
            return;
        }

        try
        {
            var delay = _services.Clipboard.Copy(text, kind);
            string what = kind switch
            {
                ClipboardItemKind.Username => "Usuario copiado",
                ClipboardItemKind.VerificationCode => "Código copiado",
                _ => "Contraseña copiada",
            };
            _dialogs.ShowToast($"{what} · se borrará del portapapeles en {delay.TotalSeconds:0} s", ToastKind.Success);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _dialogs.ShowToast("No se pudo acceder al portapapeles. Inténtalo de nuevo.", ToastKind.Error);
        }
    }

    private void OpenWebsite()
    {
        if (!DomainUtil.TryCreateBrowsableUri(Detail.Url, out var uri) || uri is null)
        {
            _dialogs.ShowToast("La dirección guardada no es una web válida (solo se abren enlaces http/https).", ToastKind.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _dialogs.ShowToast("No se pudo abrir el navegador.", ToastKind.Error);
        }
    }

    private void DeleteSelected()
    {
        var entry = Detail.Entry;
        if (entry is null)
        {
            return;
        }

        if (!_dialogs.Confirm(
                $"¿Eliminar «{entry.Title}» de PassDesk?\n\nSolo se borra la copia local de PassDesk. No afecta a Contraseñas de Apple ni a tu iPhone.",
                "Eliminar credencial"))
        {
            return;
        }

        try
        {
            _services.Session.DeleteEntry(entry.Id);
            _dialogs.ShowToast("Credencial eliminada", ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowToast("No se pudo guardar el cambio en disco.", ToastKind.Error);
        }
    }
}
