using System.Security.Cryptography;

namespace PassDesk.Crypto;

// Todas las excepciones usan mensajes genéricos y fijos: nunca incluyen datos del usuario,
// fragmentos de archivo ni material criptográfico.

/// <summary>La contraseña maestra no es válida (o la cabecera de claves fue manipulada).</summary>
public sealed class InvalidMasterPasswordException : CryptographicException
{
    public InvalidMasterPasswordException()
        : base("La contraseña maestra no es correcta.")
    {
    }
}

/// <summary>El contenido cifrado no supera la verificación de integridad o tiene un formato no válido.</summary>
public sealed class VaultFormatException : Exception
{
    public VaultFormatException(string reason)
        : base(reason)
    {
    }
}

/// <summary>
/// Windows (DPAPI) no puede descifrar la capa local. Suele indicar otro usuario de Windows,
/// otro equipo o una restauración del perfil sin las claves maestras de DPAPI.
/// </summary>
public sealed class VaultAccessDeniedException : Exception
{
    public VaultAccessDeniedException(int hresult)
        : base("Windows no puede descifrar la bóveda con la cuenta de usuario actual.")
    {
        HResult = hresult;
    }
}

/// <summary>
/// No se pudo cargar libsodium, con el que se calcula Argon2id (por ejemplo, porque un antivirus o AppLocker bloquean la
/// DLL). Las bóvedas antiguas (PBKDF2) se siguen abriendo; lo que necesita Argon2id no está disponible.
/// </summary>
public sealed class CryptoUnavailableException : Exception
{
    public const string UserMessage = "No se pudo cargar el componente de cifrado (libsodium). Reinstala PassDesk o revisa el antivirus.";

    public CryptoUnavailableException(Exception inner)
        : base(UserMessage, inner)
    {
        HResult = inner.HResult;
    }
}
