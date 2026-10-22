using System.Text;
using PassDesk.Browser.Broker;
using PassDesk.Browser.Protocol;

namespace PassDesk.Auth;

/// <summary>
/// Vista de la sesión de la bóveda para el autorrelleno del navegador. No crea otra bóveda ni otra autenticación: lee la
/// sesión existente, que sigue controlando el bloqueo. Con la bóveda bloqueada no devuelve nada, y cada lectura de una
/// contraseña se hace bajo el cerrojo de la sesión (<see cref="VaultSession.TryRead{T}"/>), de modo que un bloqueo no
/// puede intercalarse a mitad de la entrega.
/// </summary>
public sealed class SessionAutofillVault : IAutofillVault, IDisposable
{
    private readonly VaultSession _session;

    public SessionAutofillVault(VaultSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.StateChanged += OnStateChanged;
    }

    public event EventHandler? Locked;

    public AutofillVaultState State => _session.State switch
    {
        VaultState.NotCreated => AutofillVaultState.NotCreated,
        VaultState.Locked => AutofillVaultState.Locked,
        _ => AutofillVaultState.Unlocked,
    };

    public IReadOnlyList<AutofillEntryInfo> Snapshot() =>
        _session.TryRead(
            entries => (IReadOnlyList<AutofillEntryInfo>)[.. entries.Select(Describe)],
            out var list)
            ? list!
            : [];

    public AutofillSecret? Read(Guid id, FillScope scope, Func<AutofillEntryInfo, bool> authorize)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        _session.TryRead(
            entries =>
            {
                var entry = entries.FirstOrDefault(e => e.Id == id);
                if (entry is null || !authorize(Describe(entry)))
                {
                    return (AutofillSecret?)null;
                }

                bool wantUser = scope is FillScope.Both or FillScope.Username;
                bool wantPassword = scope is FillScope.Both or FillScope.Password;
                byte[] user = Encoding.UTF8.GetBytes(wantUser ? entry.Username : string.Empty);
                try
                {
                    if (!wantPassword)
                    {
                        return new AutofillSecret(user, [], includeUsername: true, includePassword: false);
                    }

                    using var password = entry.Password.Unprotect();
                    return new AutofillSecret(user, password.Span, includeUsername: wantUser, includePassword: true);
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(user);
                }
            },
            out var secret);
        return secret;
    }

    public void Dispose() => _session.StateChanged -= OnStateChanged;

    private static AutofillEntryInfo Describe(Core.Model.CredentialEntry entry) =>
        new(entry.Id, entry.Title, entry.Url, entry.Username);

    private void OnStateChanged(object? sender, VaultStateChangedEventArgs e)
    {
        if (e.OldState == VaultState.Unlocked && e.NewState != VaultState.Unlocked)
        {
            Locked?.Invoke(this, EventArgs.Empty);
        }
    }
}
