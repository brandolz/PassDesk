using System.Security.Cryptography;
using PassDesk.Browser.Origins;

namespace PassDesk.Browser.Broker;

/// <summary>
/// Fichas de selección: PassDesk entrega una al listar las coincidencias de un origen, y solo con esa ficha se puede
/// pedir el relleno. Cada ficha vale para una credencial y un origen, caduca, se usa una sola vez y desaparece al
/// bloquear la bóveda. Así un clic no puede reutilizarse para otro origen, otra credencial o más tarde.
/// </summary>
internal sealed class SelectionTokens(TimeProvider time, TimeSpan lifetime, int capacity)
{
    private readonly Dictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    internal sealed record Grant(Guid CredentialId, string Origin, OriginMatchKind Kind, long Epoch, DateTimeOffset ExpiresAt);

    public string Mint(Guid credentialId, WebOrigin origin, OriginMatchKind kind, long epoch)
    {
        string token = Base64Url(RandomNumberGenerator.GetBytes(16));
        var now = time.GetUtcNow();
        lock (_gate)
        {
            Purge(now);
            while (_grants.Count >= capacity)
            {
                // Se descarta la más antigua.
                var oldest = _grants.MinBy(g => g.Value.ExpiresAt).Key;
                _grants.Remove(oldest);
            }

            _grants[token] = new Grant(credentialId, origin.Serialize(), kind, epoch, now + lifetime);
        }

        return token;
    }

    /// <summary>
    /// Consume la ficha (se elimina siempre que exista, aunque no coincida con lo pedido: presentarla para otro origen
    /// o credencial la inutiliza). Devuelve null si no existe o caducó.
    /// </summary>
    public Grant? Consume(string token)
    {
        lock (_gate)
        {
            if (!_grants.Remove(token, out var grant))
            {
                return null;
            }

            return grant.ExpiresAt > time.GetUtcNow() ? grant : null;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _grants.Clear();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _grants.Count;
            }
        }
    }

    private void Purge(DateTimeOffset now)
    {
        foreach (var expired in _grants.Where(g => g.Value.ExpiresAt <= now).Select(g => g.Key).ToList())
        {
            _grants.Remove(expired);
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
