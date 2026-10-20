using System.Security.Cryptography;

namespace PassDesk.Crypto;

/// <summary>
/// Envoltorio de DPAPI (Data Protection API de Windows) con ámbito <c>CurrentUser</c>.
/// <para>
/// DPAPI vincula los datos a la cuenta de Windows actual: solo esa cuenta, en este equipo
/// (o en el dominio, si el perfil es itinerante), puede descifrarlos. PassDesk lo usa como
/// capa externa sobre un contenido que ya está cifrado con AES-256-GCM.
/// </para>
/// </summary>
public static class WindowsDataProtection
{
    // Entropía adicional específica de la aplicación: otro programa que llame a DPAPI con la
    // misma cuenta no puede descifrar el blob sin conocer también este valor.
    private static readonly byte[] Entropy = "PassDesk/LocalVault/DPAPI/v1"u8.ToArray();

    public static byte[] Protect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    }

    public static byte[] Unprotect(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        try
        {
            return ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new VaultAccessDeniedException(ex.HResult);
        }
    }
}
