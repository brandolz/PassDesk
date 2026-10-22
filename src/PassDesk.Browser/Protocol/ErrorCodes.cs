namespace PassDesk.Browser.Protocol;

/// <summary>Códigos de error del protocolo. Son fijos y genéricos: nunca llevan datos de la solicitud.</summary>
public static class ErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string TooLarge = "too_large";
    public const string UnsupportedOrigin = "unsupported_origin";
    public const string InsecureOrigin = "insecure_origin";
    public const string Disabled = "disabled";
    public const string NotCreated = "not_created";
    public const string Locked = "locked";
    public const string NotFound = "not_found";
    public const string Denied = "denied";
    public const string RateLimited = "rate_limited";
    public const string Busy = "busy";
    public const string Expired = "expired";
    public const string Internal = "internal";

    /// <summary>Lo genera el conector cuando PassDesk no está abierto o no admite el navegador.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>Lo genera el conector cuando quien lo lanzó no es la extensión autorizada.</summary>
    public const string UnauthorizedCaller = "unauthorized_caller";
}
