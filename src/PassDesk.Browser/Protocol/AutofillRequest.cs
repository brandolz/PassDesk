using System.Text.Json;
using PassDesk.Browser.Origins;

namespace PassDesk.Browser.Protocol;

public enum RequestType
{
    /// <summary>Estado de la integración (desactivada, sin bóveda, bloqueada o lista). No devuelve datos.</summary>
    Status,

    /// <summary>Credenciales que coinciden con el origen de la página (solo servicio y usuario).</summary>
    List,

    /// <summary>Usuario y/o contraseña de una credencial ya listada, con su token de selección.</summary>
    Fill,

    /// <summary>Pide que PassDesk muestre su pantalla de desbloqueo (no desbloquea nada por sí misma).</summary>
    Unlock,
}

/// <summary>Qué datos de la credencial se necesitan. Solo se entregan los pedidos.</summary>
public enum FillScope
{
    Both,
    Username,
    Password,
}

public enum ProtocolError
{
    None,
    NotJson,
    InvalidShape,
    UnsupportedVersion,
    UnknownType,
    UnknownField,
    UnexpectedField,
    DuplicateField,
    MissingField,
    InvalidField,

    /// <summary>El origen no es http/https (página interna del navegador, extensión, archivo…).</summary>
    UnsupportedOrigin,
}

/// <summary>Solicitud validada de la extensión. Solo existe si pasó todas las comprobaciones de <see cref="AutofillRequestParser"/>.</summary>
public sealed record AutofillRequest(
    RequestType Type,
    string? RequestId,
    WebOrigin? Origin,
    Guid? CredentialId,
    string? Token,
    FillScope? Scope);

/// <summary>
/// Validador estricto de mensajes. Se ejecuta tanto en el conector como en PassDesk: ninguno confía en las
/// comprobaciones del otro ni en las de JavaScript. Solo admite un objeto plano con los campos conocidos de cada tipo,
/// sin duplicados, con tipos y longitudes exactos.
/// </summary>
public static class AutofillRequestParser
{
    /// <summary>Tamaño máximo de una solicitud. Las legítimas ocupan menos de 400 bytes.</summary>
    public const int MaxRequestBytes = 4096;

    public const int ProtocolVersion = 1;

    private const int MaxTokenLength = 64;
    private const int MinTokenLength = 22;
    private const int MaxRequestIdLength = 64;

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 2,
    };

    public static bool TryParse(ReadOnlySpan<byte> utf8, out AutofillRequest? request, out ProtocolError error)
    {
        request = null;
        if (utf8.IsEmpty || utf8.Length > MaxRequestBytes)
        {
            error = ProtocolError.InvalidShape;
            return false;
        }

        try
        {
            return TryParseCore(utf8, out request, out error);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            // Incluye UTF-8 inválido, números fuera de rango y JSON incompleto. Nunca se conserva el mensaje.
            request = null;
            error = ProtocolError.NotJson;
            return false;
        }
    }

    private static bool TryParseCore(ReadOnlySpan<byte> utf8, out AutofillRequest? request, out ProtocolError error)
    {
        request = null;
        var reader = new Utf8JsonReader(utf8, isFinalBlock: true, new JsonReaderState(ReaderOptions));
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            error = ProtocolError.InvalidShape;
            return false;
        }

        bool hasVersion = false;
        bool hasType = false;
        int version = 0;
        string? typeName = null;
        string? requestId = null;
        string? originText = null;
        string? idText = null;
        string? token = null;
        string? scopeText = null;
        bool hasRequestId = false, hasOrigin = false, hasId = false, hasToken = false, hasScope = false;

        while (true)
        {
            if (!reader.Read())
            {
                error = ProtocolError.NotJson;
                return false;
            }

            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                error = ProtocolError.InvalidShape;
                return false;
            }

            string name = reader.GetString()!;
            if (!reader.Read())
            {
                error = ProtocolError.NotJson;
                return false;
            }

            switch (name)
            {
                case "v":
                    if (hasVersion)
                    {
                        error = ProtocolError.DuplicateField;
                        return false;
                    }

                    hasVersion = true;
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out version))
                    {
                        error = ProtocolError.InvalidField;
                        return false;
                    }

                    break;
                case "type":
                    if (hasType)
                    {
                        error = ProtocolError.DuplicateField;
                        return false;
                    }

                    hasType = true;
                    if (!TryReadString(ref reader, 16, out typeName))
                    {
                        error = ProtocolError.InvalidField;
                        return false;
                    }

                    break;
                case "rid":
                    if (hasRequestId)
                    {
                        error = ProtocolError.DuplicateField;
                        return false;
                    }

                    hasRequestId = true;
                    if (!TryReadString(ref reader, MaxRequestIdLength, out requestId) || !IsUrlSafe(requestId, 1, MaxRequestIdLength))
                    {
                        error = ProtocolError.InvalidField;
                        return false;
                    }

                    break;
                case "origin":
                    if (hasOrigin)
                    {
                        error = ProtocolError.DuplicateField;
                        return false;
                    }

                    hasOrigin = true;
                    if (!TryReadString(ref reader, WebOrigin.MaxLength, out originText))
                    {
                        error = ProtocolError.InvalidField;
                        return false;
                    }

                    break;
                case "id":
                    if (hasId)
                    {
                        error = ProtocolError.DuplicateField;
                        return false;
                    }

                    hasId = true;
                    if (!TryReadString(ref reader, 36, out idText))
                    {
                        error = ProtocolError.InvalidField;
                        return false;
                    }

                    break;
                case "token":
                    if (hasToken)
                    {
                        error = ProtocolError.DuplicateField;
                        return false;
                    }

                    hasToken = true;
                    if (!TryReadString(ref reader, MaxTokenLength, out token) || !IsUrlSafe(token, MinTokenLength, MaxTokenLength))
                    {
                        error = ProtocolError.InvalidField;
                        return false;
                    }

                    break;
                case "scope":
                    if (hasScope)
                    {
                        error = ProtocolError.DuplicateField;
                        return false;
                    }

                    hasScope = true;
                    if (!TryReadString(ref reader, 16, out scopeText))
                    {
                        error = ProtocolError.InvalidField;
                        return false;
                    }

                    break;
                default:
                    error = ProtocolError.UnknownField;
                    return false;
            }
        }

        // Nada después del objeto (el lector ya rechaza varios valores seguidos; esto cubre el resto).
        if (reader.BytesConsumed != utf8.Length && !IsOnlyWhitespace(utf8[(int)reader.BytesConsumed..]))
        {
            error = ProtocolError.NotJson;
            return false;
        }

        if (!hasVersion || !hasType)
        {
            error = ProtocolError.MissingField;
            return false;
        }

        if (version != ProtocolVersion)
        {
            error = ProtocolError.UnsupportedVersion;
            return false;
        }

        RequestType type;
        switch (typeName)
        {
            case "status":
                type = RequestType.Status;
                break;
            case "list":
                type = RequestType.List;
                break;
            case "fill":
                type = RequestType.Fill;
                break;
            case "unlock":
                type = RequestType.Unlock;
                break;
            default:
                error = ProtocolError.UnknownType;
                return false;
        }

        // Cada tipo admite exactamente sus campos: ni de más ni de menos.
        bool needsOrigin = type is RequestType.List or RequestType.Fill;
        bool needsFill = type == RequestType.Fill;
        if ((hasOrigin && !needsOrigin) || ((hasId || hasToken || hasScope) && !needsFill))
        {
            error = ProtocolError.UnexpectedField;
            return false;
        }

        if ((needsOrigin && !hasOrigin) || (needsFill && !(hasId && hasToken && hasScope)))
        {
            error = ProtocolError.MissingField;
            return false;
        }

        WebOrigin? origin = null;
        if (needsOrigin)
        {
            if (!WebOrigin.TryParse(originText, out origin, out var originError))
            {
                error = originError == OriginError.UnsupportedScheme ? ProtocolError.UnsupportedOrigin : ProtocolError.InvalidField;
                return false;
            }
        }

        Guid? credentialId = null;
        FillScope? scope = null;
        if (needsFill)
        {
            if (!Guid.TryParseExact(idText, "D", out var id) || id == Guid.Empty)
            {
                error = ProtocolError.InvalidField;
                return false;
            }

            credentialId = id;
            scope = scopeText switch
            {
                "both" => FillScope.Both,
                "username" => FillScope.Username,
                "password" => FillScope.Password,
                _ => null,
            };
            if (scope is null)
            {
                error = ProtocolError.InvalidField;
                return false;
            }
        }

        error = ProtocolError.None;
        request = new AutofillRequest(type, requestId, origin, credentialId, needsFill ? token : null, scope);
        return true;
    }

    private static bool TryReadString(ref Utf8JsonReader reader, int maxChars, out string? value)
    {
        value = null;
        if (reader.TokenType != JsonTokenType.String)
        {
            return false;
        }

        value = reader.GetString();
        return value is not null && value.Length <= maxChars;
    }

    private static bool IsUrlSafe(string? text, int min, int max)
    {
        if (text is null || text.Length < min || text.Length > max)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOnlyWhitespace(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b is not (0x20 or 0x09 or 0x0A or 0x0D))
            {
                return false;
            }
        }

        return true;
    }
}
