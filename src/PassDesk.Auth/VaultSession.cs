using System.Security.Cryptography;
using PassDesk.Core.Diagnostics;
using PassDesk.Core.Model;
using PassDesk.Core.Services;
using PassDesk.Crypto;
using PassDesk.Storage;

namespace PassDesk.Auth;

public enum VaultState
{
    NotCreated,
    Locked,
    Unlocked,
}

public enum LockReason
{
    Manual,
    Inactivity,
    SessionLocked,
    Suspend,
    Minimized,
    Exit,
}

public enum UnlockStatus
{
    Success,
    WrongPassword,
    Throttled,
    WeakPassword,
    AlreadyExists,
    VaultUnreadable,
    VaultCorrupted,
    NotAvailable,
    Canceled,

    /// <summary>No se pudo cargar libsodium (Argon2id). No cuenta como intento fallido.</summary>
    CryptoUnavailable,
}

public sealed record UnlockResult(UnlockStatus Status, TimeSpan RetryAfter = default)
{
    public static UnlockResult Ok { get; } = new(UnlockStatus.Success);

    public bool Succeeded => Status == UnlockStatus.Success;
}

public sealed class VaultStateChangedEventArgs(VaultState oldState, VaultState newState, LockReason? reason) : EventArgs
{
    public VaultState OldState { get; } = oldState;

    public VaultState NewState { get; } = newState;

    public LockReason? Reason { get; } = reason;
}

/// <summary>
/// Sesión de la bóveda: controla creación, desbloqueo, bloqueo y persistencia.
/// <para>
/// Mientras está bloqueada no hay credenciales descifradas en memoria. Si el desbloqueo rápido
/// está activado, se conserva solo la clave de datos (cifrada con CryptProtectMemory) para poder
/// desbloquear con Windows Hello, como mucho durante <see cref="QuickUnlockLifetime"/> desde la última vez que
/// se escribió la contraseña maestra, y nunca después de bloquear Windows, suspender el equipo o reiniciar la app.
/// </para>
/// </summary>
public sealed class VaultSession : IDisposable
{
    /// <summary>Tiempo máximo que Windows Hello puede sustituir a la contraseña maestra.</summary>
    public static readonly TimeSpan DefaultQuickUnlockLifetime = TimeSpan.FromHours(8);

    private readonly IVaultStore _store;
    private readonly ISecurityStateStore _securityState;
    private readonly IClock _clock;
    private readonly KdfSettings _kdf;
    private readonly Lock _sync = new();
    private VaultKeyring? _keyring;
    private VaultDocument? _document;
    private VaultState _state;
    private bool _quickUnlockEnabled = true;
    private TimeSpan _quickUnlockLifetime = DefaultQuickUnlockLifetime;
    private DateTimeOffset _passwordVerifiedAt;
    private bool? _masterPasswordMeetsPolicy;
    private UnlockThrottleState _throttle;

    /// <summary>Sesión con los parámetros de derivación por defecto (Argon2id).</summary>
    public VaultSession(IVaultStore store, ISecurityStateStore securityState, IClock clock)
        : this(store, securityState, clock, KdfSettings.Default)
    {
    }

    /// <summary>Sesión con PBKDF2 y el número de iteraciones indicado (compatibilidad y pruebas rápidas).</summary>
    public VaultSession(IVaultStore store, ISecurityStateStore securityState, IClock clock, int iterations)
        : this(store, securityState, clock, KdfSettings.Pbkdf2(iterations))
    {
    }

    public VaultSession(IVaultStore store, ISecurityStateStore securityState, IClock clock, KdfSettings kdf)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _securityState = securityState ?? throw new ArgumentNullException(nameof(securityState));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _kdf = kdf ?? throw new ArgumentNullException(nameof(kdf));
        if (!kdf.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(kdf));
        }

        _state = store.Exists ? VaultState.Locked : VaultState.NotCreated;
        _throttle = ReadPersistedThrottle();
    }

    internal delegate VaultKeyring KeyringRewrap(VaultKeyring current, ReadOnlySpan<char> password, KdfSettings kdf);

    public event EventHandler<VaultStateChangedEventArgs>? StateChanged;

    public event EventHandler? DataChanged;

    /// <summary>Re-envoltura del llavero al pasarlo a Argon2id. Solo las pruebas la sustituyen, para simular que falla.</summary>
    internal KeyringRewrap RewrapKeyring { get; set; } = static (current, password, kdf) => current.Rewrap(password, kdf);

    public VaultState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>Permite desbloquear con Windows Hello tras un primer desbloqueo con contraseña.</summary>
    public bool QuickUnlockEnabled
    {
        get
        {
            lock (_sync)
            {
                return _quickUnlockEnabled;
            }
        }

        set
        {
            lock (_sync)
            {
                _quickUnlockEnabled = value;
                if (!value && _state != VaultState.Unlocked)
                {
                    ForgetKeyring();
                }
            }
        }
    }

    /// <summary>Tiempo máximo, desde la última contraseña maestra, en que se admite Windows Hello.</summary>
    public TimeSpan QuickUnlockLifetime
    {
        get
        {
            lock (_sync)
            {
                return _quickUnlockLifetime;
            }
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            lock (_sync)
            {
                _quickUnlockLifetime = value;
            }
        }
    }

    public bool CanQuickUnlock
    {
        get
        {
            lock (_sync)
            {
                return _state == VaultState.Locked && _quickUnlockEnabled && _keyring is not null && !QuickUnlockExpired();
            }
        }
    }

    /// <summary>
    /// Si la contraseña maestra escrita en esta sesión cumple la política actual (null si todavía no se ha escrito).
    /// Las bóvedas creadas con la política anterior pueden tener una contraseña más débil.
    /// </summary>
    public bool? MasterPasswordMeetsPolicy
    {
        get
        {
            lock (_sync)
            {
                return _masterPasswordMeetsPolicy;
            }
        }
    }

    /// <summary>Instantánea de las credenciales (solo con la bóveda desbloqueada).</summary>
    public IReadOnlyList<CredentialEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return [.. EnsureUnlocked().Entries];
            }
        }
    }

    /// <summary>
    /// Ejecuta una lectura de las credenciales bajo el mismo cerrojo que el bloqueo: o la bóveda sigue desbloqueada durante
    /// toda la lectura, o no se lee nada. Devuelve false, sin llamar a <paramref name="read"/>, si está bloqueada. Lo usa el
    /// autorrelleno del navegador para que ninguna solicitud pueda obtener una contraseña después de bloquear.
    /// <paramref name="read"/> debe ser breve y no puede conservar las entradas: se liberan al bloquear.
    /// </summary>
    public bool TryRead<T>(Func<IReadOnlyList<CredentialEntry>, T> read, out T? result)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_sync)
        {
            if (_state != VaultState.Unlocked || _document is null)
            {
                result = default;
                return false;
            }

            result = read(_document.Entries);
            return true;
        }
    }

    /// <summary>Instantánea de los documentos guardados (solo con la bóveda desbloqueada).</summary>
    public IReadOnlyList<SecureDocument> Documents
    {
        get
        {
            lock (_sync)
            {
                return [.. EnsureUnlocked().Documents];
            }
        }
    }

    public ImportRecord? LastImport
    {
        get
        {
            lock (_sync)
            {
                return _document?.LastImport;
            }
        }
    }

    public UnlockResult CreateVault(ReadOnlySpan<char> password)
    {
        VaultStateChangedEventArgs change;
        lock (_sync)
        {
            if (_state != VaultState.NotCreated || _store.Exists)
            {
                return new UnlockResult(UnlockStatus.AlreadyExists);
            }

            if (!MasterPasswordPolicy.IsAcceptable(password))
            {
                return new UnlockResult(UnlockStatus.WeakPassword);
            }

            VaultKeyring keyring;
            try
            {
                keyring = VaultKeyring.Create(password, _kdf);
            }
            catch (CryptoUnavailableException ex)
            {
                return CryptoUnavailable(ex);
            }

            var document = new VaultDocument();
            try
            {
                Persist(keyring, document);
            }
            catch
            {
                keyring.Dispose();
                document.Dispose();
                throw;
            }

            _keyring = keyring;
            _document = document;
            PasswordVerified(password);
            change = Transition(VaultState.Unlocked, null);

            // Bóveda nueva, contador nuevo: el de una bóveda anterior (archivada) ya no protege nada.
            if (_throttle.FailedAttempts > 0 || _throttle.LockedUntilUtc is not null)
            {
                SaveThrottle(UnlockThrottleState.None);
            }
        }

        AppLog.Info(LogEvent.VaultCreated);
        StateChanged?.Invoke(this, change);
        return UnlockResult.Ok;
    }

    public UnlockResult Unlock(ReadOnlySpan<char> password)
    {
        VaultStateChangedEventArgs change;
        int count;
        lock (_sync)
        {
            if (_state == VaultState.Unlocked)
            {
                return UnlockResult.Ok;
            }

            if (_state == VaultState.NotCreated)
            {
                return new UnlockResult(UnlockStatus.NotAvailable);
            }

            // Antes que la espera: leer el archivo no comprueba la contraseña, y una bóveda que este equipo no puede
            // descifrar (por ejemplo, copiada de otro PC) se avisa en el momento.
            if (!TryReadEnvelope(out byte[] envelope, out var readFailure))
            {
                return readFailure!;
            }

            var now = _clock.UtcNow;
            var throttle = LoadThrottle();
            if (throttle.LockedUntilUtc is { } until && until > now)
            {
                AppLog.Warn(LogEvent.VaultUnlockThrottled, new LogMetric("waitSeconds", (long)Math.Ceiling((until - now).TotalSeconds)));
                return new UnlockResult(UnlockStatus.Throttled, until - now);
            }

            VaultKeyring keyring;
            try
            {
                keyring = VaultKeyring.Unlock(VaultKeyring.ReadHeader(envelope), password);
            }
            catch (VaultFormatException ex)
            {
                AppLog.Error(LogEvent.VaultLoadFailed, ex);
                return new UnlockResult(UnlockStatus.VaultCorrupted);
            }
            catch (InvalidMasterPasswordException)
            {
                return RegisterFailure(throttle, now);
            }
            catch (CryptoUnavailableException ex)
            {
                return CryptoUnavailable(ex);
            }

            if (!TryOpen(keyring, envelope, out var document))
            {
                keyring.Dispose();
                return new UnlockResult(UnlockStatus.VaultCorrupted);
            }

            try
            {
                if (throttle.FailedAttempts > 0 || throttle.LockedUntilUtc is not null)
                {
                    SaveThrottle(UnlockThrottleState.None);
                }

                // Las bóvedas antiguas (PBKDF2) pasan a Argon2id ahora que se dispone de la contraseña.
                if (keyring.Header.Kdf.IsWeakerThan(_kdf))
                {
                    keyring = UpgradeKeyring(keyring, document!, password);
                }
            }
            catch
            {
                // Solo llegan errores fatales (la mejora es de mejor esfuerzo): no se deja nada descifrado en memoria.
                keyring.Dispose();
                document!.Dispose();
                throw;
            }

            ForgetKeyring();
            _keyring = keyring;
            _document = document;
            PasswordVerified(password);
            count = document!.Entries.Count;
            change = Transition(VaultState.Unlocked, null);
        }

        AppLog.Info(LogEvent.VaultUnlocked, new LogMetric("entries", count));
        StateChanged?.Invoke(this, change);
        return UnlockResult.Ok;
    }

    /// <summary>Desbloqueo rápido con Windows Hello (solo si la clave sigue en memoria y no ha caducado).</summary>
    public async Task<UnlockResult> QuickUnlockAsync(IUserPresenceVerifier verifier, string message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (!CanQuickUnlock)
        {
            ExpireQuickUnlockIfDue();
            return new UnlockResult(UnlockStatus.NotAvailable);
        }

        var presence = await verifier.VerifyAsync(message, cancellationToken).ConfigureAwait(false);
        AppLog.Info(LogEvent.WindowsHelloResult, new LogMetric("result", (long)presence));
        if (presence != PresenceResult.Verified)
        {
            return new UnlockResult(presence == PresenceResult.Canceled ? UnlockStatus.Canceled : UnlockStatus.NotAvailable);
        }

        VaultStateChangedEventArgs change;
        int count;
        lock (_sync)
        {
            if (_state == VaultState.Unlocked)
            {
                return UnlockResult.Ok;
            }

            if (_state != VaultState.Locked || _keyring is null || !_quickUnlockEnabled)
            {
                return new UnlockResult(UnlockStatus.NotAvailable);
            }

            // Pudo caducar (o bloquearse Windows) mientras se mostraba el diálogo de Windows Hello.
            if (QuickUnlockExpired())
            {
                ForgetKeyring();
                AppLog.Info(LogEvent.QuickUnlockExpired);
                return new UnlockResult(UnlockStatus.NotAvailable);
            }

            if (!TryReadEnvelope(out byte[] envelope, out var readFailure))
            {
                return readFailure!;
            }

            if (!TryOpen(_keyring, envelope, out var document))
            {
                return new UnlockResult(UnlockStatus.VaultCorrupted);
            }

            _document = document;
            count = document!.Entries.Count;
            change = Transition(VaultState.Unlocked, null);
        }

        AppLog.Info(LogEvent.VaultQuickUnlock, new LogMetric("entries", count));
        StateChanged?.Invoke(this, change);
        return UnlockResult.Ok;
    }

    /// <summary>Olvida la clave del desbloqueo rápido si ha caducado. Se llama periódicamente.</summary>
    public void ExpireQuickUnlockIfDue()
    {
        lock (_sync)
        {
            if (_state != VaultState.Unlocked && _keyring is not null && QuickUnlockExpired())
            {
                ForgetKeyring();
                AppLog.Info(LogEvent.QuickUnlockExpired);
            }
        }
    }

    /// <summary>
    /// Olvida la clave del desbloqueo rápido si la bóveda está bloqueada (por ejemplo, al bloquear Windows aunque
    /// esté desactivado «Bloquear al bloquear Windows»): a partir de ahí se exige la contraseña maestra.
    /// </summary>
    public void ForgetQuickUnlockKey()
    {
        lock (_sync)
        {
            if (_state != VaultState.Unlocked)
            {
                ForgetKeyring();
            }
        }
    }

    /// <summary>
    /// Vuelve a comprobar si existe la bóveda en disco. Se usa tras archivar una bóveda que Windows no
    /// puede descifrar (otro usuario o equipo) para empezar con una nueva y restaurar una copia.
    /// </summary>
    public void RefreshStoreState()
    {
        VaultStateChangedEventArgs change;
        lock (_sync)
        {
            if (_state != VaultState.Locked || _store.Exists)
            {
                return;
            }

            ForgetKeyring();
            change = Transition(VaultState.NotCreated, null);
        }

        StateChanged?.Invoke(this, change);
    }

    /// <summary>
    /// Bloquea: borra de memoria todas las credenciales descifradas. Al salir, al bloquear Windows o al suspender
    /// el equipo también se olvida la clave del desbloqueo rápido, aunque la bóveda ya estuviera bloqueada.
    /// </summary>
    public void Lock(LockReason reason)
    {
        VaultStateChangedEventArgs change;
        lock (_sync)
        {
            bool forgetKey = !_quickUnlockEnabled || reason is LockReason.Exit or LockReason.SessionLocked or LockReason.Suspend;
            if (_state != VaultState.Unlocked)
            {
                if (forgetKey)
                {
                    ForgetKeyring();
                }

                return;
            }

            _document?.Dispose();
            _document = null;
            if (forgetKey)
            {
                ForgetKeyring();
            }

            change = Transition(VaultState.Locked, reason);
        }

        AppLog.Info(LogEvent.VaultLocked, new LogMetric("reason", (long)reason));
        StateChanged?.Invoke(this, change);
    }

    /// <summary>
    /// Aplica un cambio al documento y lo guarda cifrado de inmediato. Si el cambio o el guardado fallan, la
    /// bóveda en memoria vuelve exactamente al estado guardado en disco (no quedan cambios a medias ni objetos
    /// liberados que impidan guardar después).
    /// </summary>
    public T Mutate<T>(Func<VaultDocument, T> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        T result;
        bool rolledBack = false;
        try
        {
            lock (_sync)
            {
                var document = EnsureUnlocked();
                using var before = VaultDocumentSerializer.Serialize(document);
                try
                {
                    result = mutation(document);
                    Persist(_keyring!, document);
                }
                catch
                {
                    _document = VaultDocumentSerializer.Deserialize(before.Span);
                    document.Dispose();
                    rolledBack = true;
                    AppLog.Warn(LogEvent.VaultChangeRolledBack);
                    throw;
                }
            }
        }
        finally
        {
            if (rolledBack)
            {
                DataChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public void Mutate(Action<VaultDocument> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        Mutate(document =>
        {
            mutation(document);
            return true;
        });
    }

    public void AddEntry(CredentialEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Mutate(document => document.Entries.Add(entry));
        AppLog.Info(LogEvent.EntryAdded);
    }

    public bool DeleteEntry(Guid id)
    {
        bool removed = Mutate(document =>
        {
            var entry = document.Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null)
            {
                return false;
            }

            document.Entries.Remove(entry);
            entry.Dispose();
            return true;
        });

        if (removed)
        {
            AppLog.Info(LogEvent.EntryDeleted);
        }

        return removed;
    }

    /// <summary>
    /// Guarda una copia de la bóveda tal como está (igual de cifrada) antes de un cambio masivo, para poder
    /// deshacerlo restaurándola desde Ajustes.
    /// </summary>
    public void CreateRecoveryPoint(string reason)
    {
        lock (_sync)
        {
            EnsureUnlocked();
            _store.CreateRecoveryPoint(reason);
        }

        AppLog.Info(LogEvent.RecoveryPointCreated);
    }

    public UnlockResult ChangeMasterPassword(ReadOnlySpan<char> currentPassword, ReadOnlySpan<char> newPassword)
    {
        lock (_sync)
        {
            var document = EnsureUnlocked();
            var now = _clock.UtcNow;
            var throttle = LoadThrottle();
            if (throttle.LockedUntilUtc is { } until && until > now)
            {
                return new UnlockResult(UnlockStatus.Throttled, until - now);
            }

            try
            {
                using var check = VaultKeyring.Unlock(_keyring!.Header, currentPassword);
            }
            catch (InvalidMasterPasswordException)
            {
                return RegisterFailure(throttle, now);
            }
            catch (CryptoUnavailableException ex)
            {
                return CryptoUnavailable(ex);
            }

            if (!MasterPasswordPolicy.IsAcceptable(newPassword))
            {
                return new UnlockResult(UnlockStatus.WeakPassword);
            }

            // Nueva clave de datos: las copias antiguas del archivo no sirven para leer las nuevas.
            VaultKeyring replacement;
            try
            {
                replacement = VaultKeyring.Create(newPassword, _kdf);
            }
            catch (CryptoUnavailableException ex)
            {
                return CryptoUnavailable(ex);
            }

            try
            {
                Persist(replacement, document);
            }
            catch
            {
                replacement.Dispose();
                throw;
            }

            _keyring!.Dispose();
            _keyring = replacement;
            PasswordVerified(newPassword);
            SaveThrottle(UnlockThrottleState.None);

            // Segundo guardado: la versión anterior (vault.pdv.bak), que aún se abría con la contraseña antigua, pasa a
            // ser la nueva. Si falla, el cambio ya está guardado y la anterior se sustituirá en el próximo guardado.
            try
            {
                Persist(replacement, document);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
            }
        }

        AppLog.Info(LogEvent.MasterPasswordChanged);
        return UnlockResult.Ok;
    }

    /// <summary>Crea el sobre de una copia de seguridad, cifrado con la contraseña maestra actual.</summary>
    public byte[] CreateBackupEnvelope()
    {
        lock (_sync)
        {
            var document = EnsureUnlocked();
            using var json = VaultDocumentSerializer.Serialize(document);
            return _keyring!.Seal(json.Span);
        }
    }

    /// <summary>Abre el sobre de una copia de seguridad con su contraseña. El llamador libera el documento.</summary>
    public static VaultDocument OpenBackupEnvelope(byte[] envelope, ReadOnlySpan<char> password)
    {
        var header = VaultKeyring.ReadHeader(envelope);
        using var keyring = VaultKeyring.Unlock(header, password);
        using var plain = keyring.Open(envelope);
        return VaultDocumentSerializer.Deserialize(plain.Span);
    }

    public void Dispose()
    {
        Lock(LockReason.Exit);
        lock (_sync)
        {
            ForgetKeyring();
        }
    }

    private VaultDocument EnsureUnlocked() =>
        _state == VaultState.Unlocked && _document is not null
            ? _document
            : throw new InvalidOperationException("La bóveda está bloqueada.");

    private VaultStateChangedEventArgs Transition(VaultState newState, LockReason? reason)
    {
        var args = new VaultStateChangedEventArgs(_state, newState, reason);
        _state = newState;
        return args;
    }

    private void PasswordVerified(ReadOnlySpan<char> password)
    {
        _passwordVerifiedAt = _clock.UtcNow;
        _masterPasswordMeetsPolicy = MasterPasswordPolicy.IsAcceptable(password);
    }

    /// <summary>Caducado si pasó el plazo o si el reloj ha retrocedido desde la última contraseña.</summary>
    private bool QuickUnlockExpired()
    {
        var elapsed = _clock.UtcNow - _passwordVerifiedAt;
        return elapsed < TimeSpan.Zero || elapsed > _quickUnlockLifetime;
    }

    private void ForgetKeyring()
    {
        _keyring?.Dispose();
        _keyring = null;
    }

    /// <summary>
    /// Vuelve a proteger la clave de datos con los parámetros actuales y guarda la bóveda. Es de mejor esfuerzo y
    /// nunca impide desbloquear: si algo falla (libsodium no carga, el guardado falla…), se sigue con el llavero
    /// anterior, la bóveda en disco sigue siendo válida y se intentará en el próximo desbloqueo.
    /// </summary>
    private VaultKeyring UpgradeKeyring(VaultKeyring current, VaultDocument document, ReadOnlySpan<char> password)
    {
        VaultKeyring? upgraded = null;
        try
        {
            upgraded = RewrapKeyring(current, password, _kdf);
            Persist(upgraded, document);
        }
        catch (Exception ex)
        {
            upgraded?.Dispose();
            if (ex is OutOfMemoryException)
            {
                throw;
            }

            AppLog.Warn(LogEvent.VaultKdfUpgradeFailed);
            return current;
        }

        current.Dispose();
        AppLog.Info(LogEvent.VaultKdfUpgraded, new LogMetric("kdf", (long)_kdf.Algorithm));
        return upgraded;
    }

    private static UnlockResult CryptoUnavailable(CryptoUnavailableException ex)
    {
        AppLog.Error(LogEvent.CryptoUnavailable, ex);
        return new UnlockResult(UnlockStatus.CryptoUnavailable);
    }

    private void Persist(VaultKeyring keyring, VaultDocument document)
    {
        try
        {
            using var json = VaultDocumentSerializer.Serialize(document);
            _store.WriteEnvelope(keyring.Seal(json.Span));
            AppLog.Info(LogEvent.VaultSaved, new LogMetric("entries", document.Entries.Count));
        }
        catch (Exception ex)
        {
            AppLog.Error(LogEvent.VaultSaveFailed, ex);
            throw;
        }
    }

    private bool TryReadEnvelope(out byte[] envelope, out UnlockResult? failure)
    {
        try
        {
            envelope = _store.ReadEnvelope();
            failure = null;
            return true;
        }
        catch (VaultAccessDeniedException ex)
        {
            AppLog.Error(LogEvent.VaultLoadFailed, ex);
            failure = new UnlockResult(UnlockStatus.VaultUnreadable);
        }
        catch (VaultFormatException ex)
        {
            AppLog.Error(LogEvent.VaultLoadFailed, ex);
            failure = new UnlockResult(UnlockStatus.VaultCorrupted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error(LogEvent.VaultLoadFailed, ex);
            failure = new UnlockResult(UnlockStatus.VaultUnreadable);
        }

        envelope = [];
        return false;
    }

    private static bool TryOpen(VaultKeyring keyring, byte[] envelope, out VaultDocument? document)
    {
        try
        {
            using var plain = keyring.Open(envelope);
            document = VaultDocumentSerializer.Deserialize(plain.Span);
            return true;
        }
        catch (VaultFormatException ex)
        {
            AppLog.Error(LogEvent.VaultLoadFailed, ex);
            document = null;
            return false;
        }
    }

    private UnlockResult RegisterFailure(UnlockThrottleState current, DateTimeOffset now)
    {
        // Sin desbordamiento: un contador al máximo no puede pasar a negativo (sin espera).
        int failures = current.FailedAttempts == int.MaxValue ? int.MaxValue : current.FailedAttempts + 1;
        var delay = UnlockThrottle.DelayAfter(failures);
        SaveThrottle(new UnlockThrottleState(failures, delay > TimeSpan.Zero ? now + delay : null));
        AppLog.Warn(LogEvent.VaultUnlockFailed, new LogMetric("failures", failures));
        return new UnlockResult(UnlockStatus.WrongPassword, delay);
    }

    private UnlockThrottleState LoadThrottle() => _throttle;

    private void SaveThrottle(UnlockThrottleState state)
    {
        _throttle = state;
        try
        {
            _securityState.Save(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Si no se puede persistir, la limitación sigue aplicándose en esta ejecución (memoria).
        }
    }

    /// <summary>
    /// Sin contador guardado, el estado inicial. Si está dañado, no se puede leer o tiene valores imposibles, se aplica
    /// la espera máxima (fallo cerrado) en lugar de volver a cero, y se guarda para que reiniciar no la acorte.
    /// </summary>
    private UnlockThrottleState ReadPersistedThrottle()
    {
        var now = _clock.UtcNow;
        try
        {
            var state = _securityState.Load();
            if (state.FailedAttempts >= 0 && (state.LockedUntilUtc ?? now) <= now + UnlockThrottle.MaxDelay)
            {
                return state;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
        }

        AppLog.Warn(LogEvent.SecurityStateInvalid);
        var closed = new UnlockThrottleState(UnlockThrottle.MaxDelayAttempts, now + UnlockThrottle.MaxDelay);
        SaveThrottle(closed);
        return closed;
    }
}
