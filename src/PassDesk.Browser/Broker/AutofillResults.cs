using PassDesk.Browser.Origins;
using PassDesk.Browser.Protocol;

namespace PassDesk.Browser.Broker;

/// <summary>Respuesta del intermediario. El llamador la serializa, la escribe y la libera (pone a cero lo que contenga).</summary>
public abstract class AutofillResult : IDisposable
{
    public string? RequestId { get; init; }

    public abstract bool IsOk { get; }

    /// <summary>JSON de la respuesta en UTF-8, sobre memoria fijada que el llamador libera.</summary>
    public PinnedBuffer Serialize()
    {
        var buffer = new PinnedBuffer(512);
        try
        {
            var writer = new JsonBufferWriter(buffer);
            writer.BeginObject();
            writer.Number("v", AutofillRequestParser.ProtocolVersion);
            if (RequestId is not null)
            {
                writer.String("rid", RequestId);
            }

            writer.Bool("ok", IsOk);
            WriteBody(writer);
            writer.EndObject();
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    public virtual void Dispose()
    {
    }

    internal abstract void WriteBody(JsonBufferWriter writer);
}

public sealed class ErrorResult(string code) : AutofillResult
{
    public string Code { get; } = code;

    public override bool IsOk => false;

    internal override void WriteBody(JsonBufferWriter writer) => writer.String("error", Code);
}

/// <summary>Estado de la integración: <c>disabled</c>, <c>not_created</c>, <c>locked</c> o <c>ready</c>.</summary>
public sealed class StatusResult(string state, bool unlockRequested = false) : AutofillResult
{
    public string State { get; } = state;

    public bool UnlockRequested { get; } = unlockRequested;

    public override bool IsOk => true;

    internal override void WriteBody(JsonBufferWriter writer)
    {
        writer.String("state", State);
        if (UnlockRequested)
        {
            writer.Bool("unlockRequested", true);
        }
    }
}

/// <summary>Coincidencia para mostrar en la sugerencia: servicio y usuario, nunca la contraseña.</summary>
public sealed record AutofillMatch(Guid Id, string Title, string Username, string Site, OriginMatchKind Kind, string Token);

public sealed class ListResult(WebOrigin origin, IReadOnlyList<AutofillMatch> matches) : AutofillResult
{
    public WebOrigin Origin { get; } = origin;

    public IReadOnlyList<AutofillMatch> Matches { get; } = matches;

    public override bool IsOk => true;

    internal override void WriteBody(JsonBufferWriter writer)
    {
        writer.String("state", "ready");
        writer.String("origin", Origin.Serialize());
        writer.BeginArray("matches");
        foreach (var match in Matches)
        {
            writer.BeginElementObject();
            writer.String("id", match.Id.ToString("D"));
            writer.String("title", match.Title);
            writer.String("username", match.Username);
            writer.String("site", match.Site);
            writer.String("kind", match.Kind == OriginMatchKind.Exact ? "exact" : "related");
            writer.String("token", match.Token);
            writer.EndObject();
        }

        writer.EndArray();
    }
}

/// <summary>Usuario y/o contraseña pedidos. Lleva secretos: liberar tras escribir la respuesta.</summary>
public sealed class SecretResult(AutofillSecret secret) : AutofillResult
{
    public override bool IsOk => true;

    internal override void WriteBody(JsonBufferWriter writer)
    {
        if (secret.HasUsername)
        {
            writer.Utf8String("username", secret.Username);
        }

        if (secret.HasPassword)
        {
            writer.Utf8String("password", secret.Password);
        }
    }

    public override void Dispose() => secret.Dispose();
}
