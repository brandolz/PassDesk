using System.Security.Cryptography;
using PassDesk.Browser.Origins;
using PassDesk.Browser.Protocol;

namespace PassDesk.Browser.Broker;

public enum AutofillVaultState
{
    NotCreated,
    Locked,
    Unlocked,
}

/// <summary>Datos no secretos de una credencial (se muestran en las listas de PassDesk).</summary>
public sealed record AutofillEntryInfo(Guid Id, string Title, string Url, string Username);

/// <summary>
/// Usuario y/o contraseña en UTF-8 sobre memoria fijada. Se pone a cero al liberarlo. Solo contiene lo que pidió la
/// solicitud (<see cref="FillScope"/>): si se pidió la contraseña, el usuario es null, y al revés.
/// </summary>
public sealed class AutofillSecret : IDisposable
{
    private byte[]? _username;
    private byte[]? _password;

    public AutofillSecret(ReadOnlySpan<byte> username, ReadOnlySpan<byte> password, bool includeUsername, bool includePassword)
    {
        if (includeUsername)
        {
            _username = GC.AllocateArray<byte>(Math.Max(username.Length, 1), pinned: true);
            username.CopyTo(_username);
            UsernameLength = username.Length;
        }

        if (includePassword)
        {
            _password = GC.AllocateArray<byte>(Math.Max(password.Length, 1), pinned: true);
            password.CopyTo(_password);
            PasswordLength = password.Length;
        }
    }

    private int UsernameLength { get; }

    private int PasswordLength { get; }

    public bool HasUsername => _username is not null;

    public bool HasPassword => _password is not null;

    public ReadOnlySpan<byte> Username => _username is null ? default : _username.AsSpan(0, UsernameLength);

    public ReadOnlySpan<byte> Password => _password is null ? default : _password.AsSpan(0, PasswordLength);

    public void Dispose()
    {
        if (_username is not null)
        {
            CryptographicOperations.ZeroMemory(_username);
            _username = null;
        }

        if (_password is not null)
        {
            CryptographicOperations.ZeroMemory(_password);
            _password = null;
        }
    }
}

/// <summary>
/// Vista mínima de la bóveda para el autorrelleno. No expone la exportación ni la enumeración libre: solo una
/// instantánea sin secretos (vacía si está bloqueada) y la lectura de una credencial concreta, que solo se entrega si
/// <paramref name="authorize"/> la aprueba y la bóveda sigue desbloqueada en ese mismo instante.
/// </summary>
public interface IAutofillVault
{
    AutofillVaultState State { get; }

    /// <summary>Se produce cada vez que la bóveda deja de estar desbloqueada (bloqueo manual, inactividad, suspensión…).</summary>
    event EventHandler? Locked;

    /// <summary>Datos no secretos de todas las credenciales; vacío si no está desbloqueada.</summary>
    IReadOnlyList<AutofillEntryInfo> Snapshot();

    /// <summary>
    /// Lee la credencial bajo el mismo cerrojo que el bloqueo de la bóveda: o se entrega con la bóveda desbloqueada, o
    /// no se entrega. Devuelve null si está bloqueada, no existe o <paramref name="authorize"/> la rechaza.
    /// </summary>
    AutofillSecret? Read(Guid id, FillScope scope, Func<AutofillEntryInfo, bool> authorize);
}

/// <summary>Datos que se muestran al usuario en la confirmación nativa de PassDesk.</summary>
public sealed record FillConfirmation(string Title, string Username, string StoredSite, WebOrigin PageOrigin, OriginMatchKind Kind, FillScope Scope);

/// <summary>Pide al usuario, en la interfaz de PassDesk, que confirme el relleno.</summary>
public interface IFillConfirmer
{
    Task<bool> ConfirmAsync(FillConfirmation confirmation, CancellationToken cancellationToken);
}

/// <summary>Pide que PassDesk muestre su pantalla de bloqueo para que el usuario se autentique con los mecanismos de siempre.</summary>
public interface IUnlockRequester
{
    void RequestUnlock();
}

public enum AutofillAuditEvent
{
    Listed,
    Filled,
    Denied,
    RateLimited,
    UnlockRequested,
    BadRequest,
    ClientRejected,
    Failed,
}

/// <summary>Registro de actividad sin datos: solo el tipo de evento y un contador (por ejemplo, el número de coincidencias).</summary>
public interface IAutofillAudit
{
    void Record(AutofillAuditEvent auditEvent, int count = 0);
}

/// <summary>Ajustes que el intermediario consulta en cada solicitud, para que un cambio en Ajustes se aplique al momento.</summary>
public sealed record AutofillPolicy
{
    public bool Enabled { get; init; } = true;

    public bool AllowSubdomains { get; init; }

    /// <summary>Pide confirmación en PassDesk antes de entregar la contraseña. Activado por defecto.</summary>
    public bool RequireConfirmation { get; init; } = true;
}
