using System.Windows.Media;
using PassDesk.App.Infrastructure;
using PassDesk.Core.Model;
using PassDesk.Core.Search;
using PassDesk.Core.Security;

namespace PassDesk.App.ViewModels;

public sealed class CategoryItemViewModel(Category? category, bool isDocuments = false) : ObservableObject
{
    private int _count;

    public Category? Category { get; } = category;

    /// <summary>Entrada especial de la barra lateral que muestra los documentos cifrados.</summary>
    public bool IsDocuments { get; } = isDocuments;

    public string Name { get; } = isDocuments ? "Documentos" : category is { } c ? CategoryInfo.Name(c) : "Todas";

    public string Glyph { get; } = isDocuments ? "" : category is { } c ? CategoryInfo.Glyph(c) : CategoryInfo.AllGlyph;

    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }
}

public sealed record SortOption(SortMode Mode, string Name);

/// <summary>Elemento de la lista. Solo expone datos no secretos.</summary>
public sealed class EntryItemViewModel : ObservableObject
{
    public EntryItemViewModel(CredentialEntry entry)
    {
        Entry = entry;
        Refresh();
    }

    public CredentialEntry Entry { get; }

    public Guid Id => Entry.Id;

    public string Title { get; private set; } = string.Empty;

    public string Subtitle { get; private set; } = string.Empty;

    public string Letter { get; private set; } = string.Empty;

    public Brush MonogramBackground { get; private set; } = Brushes.Gray;

    public Brush MonogramForeground { get; private set; } = Brushes.White;

    public void Refresh()
    {
        Title = Entry.Title.Length > 0 ? Entry.Title : Entry.DisplayHost;
        Subtitle = Entry.Username.Length > 0 ? Entry.Username : Entry.DisplayHost;
        (MonogramBackground, MonogramForeground, Letter) = Monogram.For(Title, Entry.DisplayHost);
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// Panel de detalle. La contraseña se muestra oculta por defecto; al revelarla se vuelve a ocultar
/// sola a los 30 segundos, al cambiar de selección, al perder el foco la ventana o al bloquear.
/// </summary>
public sealed class EntryDetailViewModel : ObservableObject
{
    private const string Mask = "••••••••••••";
    private static readonly TimeSpan RevealDuration = TimeSpan.FromSeconds(30);

    private CredentialEntry? _entry;
    private string? _revealedPassword;
    private DateTimeOffset _revealedAt;
    private TotpGenerator? _totp;
    private string _code = string.Empty;
    private int _codeSeconds;
    private string _notes = string.Empty;

    public CredentialEntry? Entry => _entry;

    public bool HasEntry => _entry is not null;

    public string Title => _entry is null ? string.Empty : (_entry.Title.Length > 0 ? _entry.Title : _entry.DisplayHost);

    public string Url => _entry?.Url ?? string.Empty;

    public bool HasUrl => Url.Length > 0;

    public string Host => _entry?.DisplayHost ?? string.Empty;

    public string Username => _entry?.Username ?? string.Empty;

    public bool HasUsername => Username.Length > 0;

    public bool HasPassword => _entry is { Password.IsEmpty: false };

    public string PasswordText => _revealedPassword ?? Mask;

    public bool IsRevealed => _revealedPassword is not null;

    public string RevealGlyph => IsRevealed ? "" : "";

    public string RevealToolTip => IsRevealed ? "Ocultar contraseña" : "Mostrar contraseña (se ocultará en 30 s)";

    public string Notes => _notes;

    public bool HasNotes => _notes.Length > 0;

    public bool HasCode => _totp is not null;

    /// <summary>Código formateado para leer ("123 456").</summary>
    public string CodeDisplay => _code.Length == 6 ? _code[..3] + " " + _code[3..] : _code;

    public string CurrentCode => _code;

    public string CodeSecondsText => $"{_codeSeconds} s";

    public double CodeProgress => _totp is null ? 0 : (double)_codeSeconds / _totp.Period;

    public string CategoryName => _entry is null ? string.Empty : CategoryInfo.Name(_entry.Category);

    public string CategoryGlyph => _entry is null ? string.Empty : CategoryInfo.Glyph(_entry.Category);

    public string Letter { get; private set; } = string.Empty;

    public Brush MonogramBackground { get; private set; } = Brushes.Gray;

    public Brush MonogramForeground { get; private set; } = Brushes.White;

    public string ModifiedText => Formatting.Date(_entry?.ModifiedUtc);

    public string CreatedText => Formatting.Date(_entry?.CreatedUtc);

    public string ImportedText => _entry?.ImportedUtc is { } imported ? Formatting.Date(imported) : "No importada";

    public string SourceText => _entry?.Source switch
    {
        EntrySources.AppleExport => "Importada de Contraseñas de Apple",
        EntrySources.Backup => "Restaurada de una copia de seguridad",
        _ => "Creada en PassDesk",
    };

    public void Show(CredentialEntry? entry)
    {
        _revealedPassword = null;
        _totp?.Dispose();
        _totp = null;
        _code = string.Empty;
        _notes = string.Empty;
        _entry = entry;

        if (entry is not null)
        {
            _notes = entry.Notes.IsEmpty ? string.Empty : entry.Notes.Reveal();
            _totp = TotpGenerator.TryCreate(entry.OtpAuth);
            (MonogramBackground, MonogramForeground, Letter) = Monogram.For(Title, entry.DisplayHost);
            UpdateCode();
        }

        OnPropertyChanged(string.Empty);
    }

    public void Clear() => Show(null);

    public void ToggleReveal()
    {
        if (IsRevealed)
        {
            Hide();
            return;
        }

        if (_entry is null || _entry.Password.IsEmpty)
        {
            return;
        }

        _revealedPassword = _entry.Password.Reveal();
        _revealedAt = DateTimeOffset.UtcNow;
        OnPropertiesChanged(nameof(PasswordText), nameof(IsRevealed), nameof(RevealGlyph), nameof(RevealToolTip));
    }

    public void Hide()
    {
        if (_revealedPassword is null)
        {
            return;
        }

        _revealedPassword = null;
        OnPropertiesChanged(nameof(PasswordText), nameof(IsRevealed), nameof(RevealGlyph), nameof(RevealToolTip));
    }

    /// <summary>Se llama cada segundo: caducidad de la contraseña revelada y código TOTP.</summary>
    public void Tick()
    {
        if (IsRevealed && DateTimeOffset.UtcNow - _revealedAt > RevealDuration)
        {
            Hide();
        }

        if (_totp is not null)
        {
            UpdateCode();
            OnPropertiesChanged(nameof(CodeDisplay), nameof(CodeSecondsText), nameof(CodeProgress));
        }
    }

    private void UpdateCode()
    {
        if (_totp is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _code = _totp.ComputeCode(now);
        _codeSeconds = _totp.SecondsRemaining(now);
    }
}
