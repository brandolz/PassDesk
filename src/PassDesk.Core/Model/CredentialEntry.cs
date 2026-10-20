using PassDesk.Core.Text;
using PassDesk.Crypto;

namespace PassDesk.Core.Model;

public enum Category
{
    Social,
    Email,
    Gaming,
    Banking,
    Shopping,
    Other,
}

/// <summary>Identificadores del origen de cada registro.</summary>
public static class EntrySources
{
    public const string Manual = "manual";
    public const string AppleExport = "apple-export";
    public const string Backup = "backup";
}

/// <summary>
/// Credencial almacenada. Los campos sensibles (contraseña, notas y semilla TOTP) se mantienen
/// como <see cref="ProtectedSecret"/>; título, URL y usuario son texto normal porque se
/// muestran en listas y búsquedas.
/// </summary>
public sealed class CredentialEntry : IDisposable
{
    private string _title = string.Empty;
    private string _url = string.Empty;
    private string _username = string.Empty;
    private SearchFields? _search;
    private ProtectedSecret _password = ProtectedSecret.Empty;
    private ProtectedSecret _notes = ProtectedSecret.Empty;
    private ProtectedSecret _otpAuth = ProtectedSecret.Empty;

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set
        {
            _title = value?.Trim() ?? string.Empty;
            _search = null;
        }
    }

    public string Url
    {
        get => _url;
        set
        {
            _url = value?.Trim() ?? string.Empty;
            _search = null;
        }
    }

    public string Username
    {
        get => _username;
        set
        {
            _username = value?.Trim() ?? string.Empty;
            _search = null;
        }
    }

    public ProtectedSecret Password => _password;

    public ProtectedSecret Notes => _notes;

    /// <summary>URI otpauth:// del código de verificación (si existe), protegida como secreto.</summary>
    public ProtectedSecret OtpAuth => _otpAuth;

    public Category Category { get; set; } = Category.Other;

    /// <summary>Indica que el usuario eligió la categoría manualmente (no se reclasifica).</summary>
    public bool IsCategoryManual { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset ModifiedUtc { get; set; }

    public DateTimeOffset? ImportedUtc { get; set; }

    public string Source { get; set; } = EntrySources.Manual;

    /// <summary>Host sin "www." (vacío si no hay URL válida).</summary>
    public string DisplayHost => Search.Host;

    /// <summary>Clave para detectar duplicados: sitio (o título) + usuario, normalizados.</summary>
    public string DuplicateKey => Search.DuplicateKey;

    internal SearchFields Search => _search ??= SearchFields.Create(this);

    /// <summary>Sustituye la contraseña; el secreto anterior se borra de memoria.</summary>
    public void SetPassword(ProtectedSecret value) => Replace(ref _password, value);

    public void SetNotes(ProtectedSecret value) => Replace(ref _notes, value);

    public void SetOtpAuth(ProtectedSecret value) => Replace(ref _otpAuth, value);

    public void Dispose()
    {
        _password.Dispose();
        _notes.Dispose();
        _otpAuth.Dispose();
    }

    public override string ToString() => $"CredentialEntry({Id})";

    private static void Replace(ref ProtectedSecret field, ProtectedSecret value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!ReferenceEquals(field, value))
        {
            field.Dispose();
            field = value;
        }
    }

    public static string BuildDuplicateKey(string? title, string? url, string? username)
    {
        string host = DomainUtil.GetDisplayHost(url);
        string site = host.Length > 0 ? host : TextNormalizer.Fold(title).Trim();
        return site + "\u001F" + TextNormalizer.Fold(username).Trim();
    }

    /// <summary>Campos normalizados precalculados para búsquedas rápidas.</summary>
    internal sealed record SearchFields(
        string Host,
        string Title,
        string Domain,
        string Username,
        string Haystack,
        string DuplicateKey)
    {
        public static SearchFields Create(CredentialEntry entry)
        {
            string host = DomainUtil.GetDisplayHost(entry.Url);
            string title = TextNormalizer.Fold(entry.Title);
            string domain = TextNormalizer.Fold(host);
            string user = TextNormalizer.Fold(entry.Username);
            string url = TextNormalizer.Fold(entry.Url);
            string haystack = string.Join('\n', title, domain, url, user);
            return new SearchFields(host, title, domain, user, haystack, BuildDuplicateKey(entry.Title, entry.Url, entry.Username));
        }
    }
}
