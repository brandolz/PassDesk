using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using PassDesk.Browser.Broker;
using PassDesk.Browser.Origins;
using PassDesk.Browser.Protocol;

namespace PassDesk.Browser.Tests;

public class RequestParserTests
{
    private const string Token = "AbCdEfGhIjKlMnOpQrStUv";
    private const string Id = "0b0f8a52-6f7a-4b9d-9a53-1d2c3e4f5a6b";

    private static bool Parse(string json, out AutofillRequest? request, out ProtocolError error) =>
        AutofillRequestParser.TryParse(Encoding.UTF8.GetBytes(json), out request, out error);

    [Fact]
    public void Valid_requests_of_each_type_parse()
    {
        Assert.True(Parse("""{"v":1,"type":"status"}""", out var status, out _));
        Assert.Equal(RequestType.Status, status!.Type);

        Assert.True(Parse("""{"v":1,"type":"unlock","rid":"abc-123_x"}""", out var unlock, out _));
        Assert.Equal(RequestType.Unlock, unlock!.Type);
        Assert.Equal("abc-123_x", unlock.RequestId);

        Assert.True(Parse("""{"v":1,"type":"list","origin":"https://ejemplo.com:8443"}""", out var list, out _));
        Assert.Equal(new WebOrigin("https", "ejemplo.com", 8443), list!.Origin);

        Assert.True(Parse($$"""{"v":1,"type":"fill","origin":"https://ejemplo.com","id":"{{Id}}","token":"{{Token}}","scope":"password"}""", out var fill, out _));
        Assert.Equal(Guid.Parse(Id), fill!.CredentialId);
        Assert.Equal(Token, fill.Token);
        Assert.Equal(FillScope.Password, fill.Scope);
    }

    [Fact]
    public void Property_order_and_whitespace_do_not_matter()
    {
        Assert.True(Parse($$"""  {  "scope" : "both", "token":"{{Token}}", "id":"{{Id}}", "origin":"https://ejemplo.com", "type":"fill", "v":1 }  """, out var request, out _));
        Assert.Equal(FillScope.Both, request!.Scope);
    }

    [Theory]
    [InlineData("", ProtocolError.InvalidShape)]
    [InlineData("null", ProtocolError.InvalidShape)]
    [InlineData("[]", ProtocolError.InvalidShape)]
    [InlineData("42", ProtocolError.InvalidShape)]
    [InlineData("\"status\"", ProtocolError.InvalidShape)]
    [InlineData("{", ProtocolError.NotJson)]
    [InlineData("{\"v\":1,\"type\":\"status\"", ProtocolError.NotJson)]
    [InlineData("{\"v\":1,\"type\":\"status\"} {}", ProtocolError.NotJson)]
    [InlineData("{\"v\":1,\"type\":\"status\"}x", ProtocolError.NotJson)]
    [InlineData("{\"v\":1,\"type\":\"status\",}", ProtocolError.NotJson)]
    [InlineData("{\"v\":1,/*c*/\"type\":\"status\"}", ProtocolError.NotJson)]
    [InlineData("{'v':1,'type':'status'}", ProtocolError.NotJson)]
    [InlineData("{}", ProtocolError.MissingField)]
    [InlineData("{\"v\":1}", ProtocolError.MissingField)]
    [InlineData("{\"type\":\"status\"}", ProtocolError.MissingField)]
    [InlineData("{\"v\":2,\"type\":\"status\"}", ProtocolError.UnsupportedVersion)]
    [InlineData("{\"v\":0,\"type\":\"status\"}", ProtocolError.UnsupportedVersion)]
    [InlineData("{\"v\":\"1\",\"type\":\"status\"}", ProtocolError.InvalidField)]
    [InlineData("{\"v\":1.5,\"type\":\"status\"}", ProtocolError.InvalidField)]
    [InlineData("{\"v\":99999999999,\"type\":\"status\"}", ProtocolError.InvalidField)]
    [InlineData("{\"v\":true,\"type\":\"status\"}", ProtocolError.InvalidField)]
    [InlineData("{\"v\":1,\"type\":1}", ProtocolError.InvalidField)]
    [InlineData("{\"v\":1,\"type\":null}", ProtocolError.InvalidField)]
    [InlineData("{\"v\":1,\"type\":[\"status\"]}", ProtocolError.InvalidField)]
    [InlineData("{\"v\":1,\"type\":{\"a\":1}}", ProtocolError.InvalidField)]
    [InlineData("{\"v\":1,\"type\":\"STATUS\"}", ProtocolError.UnknownType)]
    [InlineData("{\"v\":1,\"type\":\"export\"}", ProtocolError.UnknownType)]
    [InlineData("{\"v\":1,\"type\":\"dump\"}", ProtocolError.UnknownType)]
    [InlineData("{\"v\":1,\"type\":\"listAll\"}", ProtocolError.UnknownType)]
    [InlineData("{\"v\":1,\"type\":\"status\",\"extra\":1}", ProtocolError.UnknownField)]
    [InlineData("{\"v\":1,\"type\":\"status\",\"__proto__\":{}}", ProtocolError.UnknownField)]
    [InlineData("{\"v\":1,\"v\":1,\"type\":\"status\"}", ProtocolError.DuplicateField)]
    [InlineData("{\"v\":1,\"type\":\"status\",\"type\":\"list\"}", ProtocolError.DuplicateField)]
    [InlineData("{\"v\":1,\"type\":\"status\",\"origin\":\"https://ejemplo.com\"}", ProtocolError.UnexpectedField)]
    [InlineData("{\"v\":1,\"type\":\"unlock\",\"origin\":\"https://ejemplo.com\"}", ProtocolError.UnexpectedField)]
    [InlineData("{\"v\":1,\"type\":\"list\",\"origin\":\"https://ejemplo.com\",\"scope\":\"both\"}", ProtocolError.UnexpectedField)]
    [InlineData("{\"v\":1,\"type\":\"list\"}", ProtocolError.MissingField)]
    [InlineData("{\"v\":1,\"type\":\"list\",\"origin\":5}", ProtocolError.InvalidField)]
    [InlineData("{\"v\":1,\"type\":\"list\",\"origin\":null}", ProtocolError.InvalidField)]
    [InlineData("{\"v\":1,\"type\":\"list\",\"origin\":\"ejemplo.com\"}", ProtocolError.InvalidField)]
    [InlineData("{\"v\":1,\"type\":\"list\",\"origin\":\"https://ejemplo.com/ruta\"}", ProtocolError.InvalidField)]
    [InlineData("{\"v\":1,\"type\":\"list\",\"origin\":\"https://ejemplo.com@evil.test\"}", ProtocolError.InvalidField)]
    [InlineData("{\"v\":1,\"type\":\"list\",\"origin\":\"chrome://settings\"}", ProtocolError.UnsupportedOrigin)]
    [InlineData("{\"v\":1,\"type\":\"list\",\"origin\":\"chrome-extension://abcdefghijklmnopabcdefghijklmnop\"}", ProtocolError.UnsupportedOrigin)]
    [InlineData("{\"v\":1,\"type\":\"list\",\"origin\":\"about://blank\"}", ProtocolError.UnsupportedOrigin)]
    [InlineData("{\"v\":1,\"type\":\"list\",\"origin\":\"file://host\"}", ProtocolError.UnsupportedOrigin)]
    [InlineData("{\"v\":1,\"type\":\"status\",\"rid\":\"\"}", ProtocolError.InvalidField)]
    [InlineData("{\"v\":1,\"type\":\"status\",\"rid\":\"con espacios\"}", ProtocolError.InvalidField)]
    [InlineData("{\"v\":1,\"type\":\"status\",\"rid\":\"<script>\"}", ProtocolError.InvalidField)]
    [InlineData("{\"v\":1,\"type\":\"status\",\"rid\":12}", ProtocolError.InvalidField)]
    public void Malformed_or_unexpected_requests_are_rejected(string json, ProtocolError expected)
    {
        Assert.False(Parse(json, out var request, out var error));
        Assert.Null(request);
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData("\"id\":\"no-es-un-guid\"")]
    [InlineData("\"id\":\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("\"id\":\"0b0f8a526f7a4b9d9a531d2c3e4f5a6b\"")]
    [InlineData("\"id\":\"{0b0f8a52-6f7a-4b9d-9a53-1d2c3e4f5a6b}\"")]
    [InlineData("\"id\":7")]
    [InlineData("\"token\":\"corto\"")]
    [InlineData("\"token\":\"con espacio en medio abc\"")]
    [InlineData("\"token\":\"AbCdEfGhIjKlMnOpQrStUv+/==\"")]
    [InlineData("\"token\":null")]
    [InlineData("\"scope\":\"all\"")]
    [InlineData("\"scope\":\"BOTH\"")]
    [InlineData("\"scope\":1")]
    public void Fill_requests_with_invalid_fields_are_rejected(string replacement)
    {
        var fields = new Dictionary<string, string>
        {
            ["id"] = $"\"id\":\"{Id}\"",
            ["token"] = $"\"token\":\"{Token}\"",
            ["scope"] = "\"scope\":\"both\"",
        };
        string key = replacement[1..replacement.IndexOf('"', 1)];
        fields[key] = replacement;
        string json = $$"""{"v":1,"type":"fill","origin":"https://ejemplo.com",{{string.Join(',', fields.Values)}}}""";
        Assert.False(Parse(json, out _, out var error));
        Assert.Equal(ProtocolError.InvalidField, error);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("token")]
    [InlineData("scope")]
    [InlineData("origin")]
    public void Fill_requests_need_every_field(string missing)
    {
        var fields = new Dictionary<string, string>
        {
            ["origin"] = "\"origin\":\"https://ejemplo.com\"",
            ["id"] = $"\"id\":\"{Id}\"",
            ["token"] = $"\"token\":\"{Token}\"",
            ["scope"] = "\"scope\":\"both\"",
        };
        fields.Remove(missing);
        string json = $$"""{"v":1,"type":"fill",{{string.Join(',', fields.Values)}}}""";
        Assert.False(Parse(json, out _, out var error));
        Assert.Equal(ProtocolError.MissingField, error);
    }

    [Fact]
    public void Oversized_messages_are_rejected_without_parsing()
    {
        byte[] big = new byte[AutofillRequestParser.MaxRequestBytes + 1];
        Array.Fill(big, (byte)' ');
        big[0] = (byte)'{';
        Assert.False(AutofillRequestParser.TryParse(big, out _, out var error));
        Assert.Equal(ProtocolError.InvalidShape, error);
        Assert.False(AutofillRequestParser.TryParse([], out _, out _));
    }

    [Fact]
    public void Very_long_strings_are_rejected()
    {
        string json = $$"""{"v":1,"type":"list","origin":"https://{{new string('a', 400)}}.com"}""";
        Assert.False(Parse(json, out _, out _));
        string rid = $$"""{"v":1,"type":"status","rid":"{{new string('a', 65)}}"}""";
        Assert.False(Parse(rid, out _, out _));
    }

    [Fact]
    public void Deeply_nested_values_are_rejected()
    {
        Assert.False(Parse("""{"v":1,"type":"status","rid":{"a":{"b":{"c":1}}}}""", out _, out _));
        Assert.False(Parse("{\"v\":1,\"type\":\"status\",\"x\":" + new string('[', 500) + new string(']', 500) + "}", out _, out _));
    }

    [Fact]
    public void Invalid_utf8_and_bom_are_rejected()
    {
        byte[] invalid = [.. "{\"v\":1,\"type\":\"list\",\"origin\":\"https://ejemplo.com\",\"rid\":\"a"u8, 0xFF, 0xFE, .. "\"}"u8];
        Assert.False(AutofillRequestParser.TryParse(invalid, out _, out _));
        byte[] bom = [0xEF, 0xBB, 0xBF, .. "{\"v\":1,\"type\":\"status\"}"u8];
        Assert.False(AutofillRequestParser.TryParse(bom, out _, out _));
    }

    [Fact]
    public void Escaped_characters_cannot_smuggle_a_different_origin()
    {
        // «@» es «@»: tras decodificar, el origen lleva usuario y se rechaza igual.
        Assert.False(Parse("""{"v":1,"type":"list","origin":"https://ejemplo.com@evil.test"}""", out _, out _));
        Assert.False(Parse("""{"v":1,"type":"list","origin":"https://ejemplo.com/ruta"}""", out _, out _));
        Assert.False(Parse("""{"v":1,"type":"list","origin":"https://ejemplo.com\u0000"}""", out _, out _));
    }

    [Fact]
    public void Rejection_never_echoes_the_message()
    {
        const string Secret = "Sentinel#Secreto-Ficticio-4821";
        Assert.False(Parse($$"""{"v":1,"type":"list","origin":"{{Secret}}"}""", out var request, out var error));
        Assert.Null(request);
        Assert.DoesNotContain(Secret, error.ToString());
    }
}

public class FramingTests
{
    private static byte[] Frame(byte[] payload)
    {
        byte[] result = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)payload.Length);
        payload.CopyTo(result, 4);
        return result;
    }

    [Fact]
    public async Task Frames_round_trip()
    {
        using var stream = new MemoryStream();
        await NativeMessageFraming.WriteAsync(stream, "{\"a\":1}"u8.ToArray(), CancellationToken.None);
        stream.Position = 0;
        var (status, buffer) = await NativeMessageFraming.ReadAsync(stream, 100, CancellationToken.None);
        Assert.Equal(FrameStatus.Ok, status);
        using (buffer)
        {
            Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(buffer!.Span));
        }
    }

    [Fact]
    public async Task Empty_stream_is_end_of_stream()
    {
        var (status, buffer) = await NativeMessageFraming.ReadAsync(new MemoryStream(), 100, CancellationToken.None);
        Assert.Equal(FrameStatus.EndOfStream, status);
        Assert.Null(buffer);
    }

    [Fact]
    public async Task Truncated_header_and_body_are_detected()
    {
        var (headerStatus, _) = await NativeMessageFraming.ReadAsync(new MemoryStream([1, 0]), 100, CancellationToken.None);
        Assert.Equal(FrameStatus.Truncated, headerStatus);

        byte[] frame = Frame("0123456789"u8.ToArray());
        var (bodyStatus, bodyBuffer) = await NativeMessageFraming.ReadAsync(new MemoryStream(frame[..8]), 100, CancellationToken.None);
        Assert.Equal(FrameStatus.Truncated, bodyStatus);
        Assert.Null(bodyBuffer);
    }

    [Fact]
    public async Task Oversized_and_zero_lengths_are_rejected_without_reading_the_body()
    {
        var oversized = new CountingStream([.. BitConverter.GetBytes(uint.MaxValue), .. new byte[16]]);
        var (status, buffer) = await NativeMessageFraming.ReadAsync(oversized, 4096, CancellationToken.None);
        Assert.Equal(FrameStatus.InvalidLength, status);
        Assert.Null(buffer);
        Assert.Equal(4, oversized.BytesRead);

        var (zeroStatus, _) = await NativeMessageFraming.ReadAsync(new MemoryStream(new byte[4]), 4096, CancellationToken.None);
        Assert.Equal(FrameStatus.InvalidLength, zeroStatus);

        var (maxStatus, maxBuffer) = await NativeMessageFraming.ReadAsync(new MemoryStream(Frame(new byte[4096])), 4096, CancellationToken.None);
        Assert.Equal(FrameStatus.Ok, maxStatus);
        maxBuffer!.Dispose();
        var (overStatus, _) = await NativeMessageFraming.ReadAsync(new MemoryStream(Frame(new byte[4097])), 4096, CancellationToken.None);
        Assert.Equal(FrameStatus.InvalidLength, overStatus);
    }

    [Fact]
    public async Task A_stalled_sender_is_cut_off_by_the_timeout()
    {
        using var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await NativeMessageFraming.ReadAsync(pipe, 100, timeout.Token));
    }

    [Fact]
    public void Buffers_are_zeroed_on_dispose()
    {
        var buffer = new PinnedBuffer(8);
        buffer.Append("secreto!"u8);
        var memory = buffer.Writable;
        Assert.Equal((byte)'s', memory.Span[0]);
        buffer.Dispose();
        Assert.Equal(0, memory.Span[0]);
        Assert.All(memory.ToArray(), b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => buffer.Span.ToArray());
    }

    [Fact]
    public void Growing_a_buffer_zeroes_the_old_one()
    {
        var buffer = new PinnedBuffer(4);
        buffer.Append("abcd"u8);
        var old = buffer.Writable;
        buffer.Append("efgh"u8);
        Assert.All(old.ToArray(), b => Assert.Equal(0, b));
        Assert.Equal("abcdefgh", Encoding.UTF8.GetString(buffer.Span));
        buffer.Dispose();
    }

    private sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        public int BytesRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var task = base.ReadAsync(buffer, cancellationToken);
            BytesRead += task.Result;
            return task;
        }
    }
}

public class ResponseSerializationTests
{
    private static JsonDocument Serialize(AutofillResult result)
    {
        using (result)
        using (var buffer = result.Serialize())
        {
            return JsonDocument.Parse(buffer.Span.ToArray());
        }
    }

    [Fact]
    public void Errors_carry_only_a_code()
    {
        using var doc = Serialize(new ErrorResult(ErrorCodes.Locked) { RequestId = "r-1" });
        var root = doc.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("locked", root.GetProperty("error").GetString());
        Assert.Equal("r-1", root.GetProperty("rid").GetString());
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal(4, root.EnumerateObject().Count());
    }

    [Fact]
    public void List_results_never_contain_a_password_field()
    {
        var origin = new WebOrigin("https", "ejemplo.com", 443);
        var matches = new[]
        {
            new AutofillMatch(Guid.NewGuid(), "Servicio \"Uno\"", "ana@ejemplo.com", "ejemplo.com", OriginMatchKind.Exact, "tok1"),
            new AutofillMatch(Guid.NewGuid(), "Dos", "beto", "login.ejemplo.com", OriginMatchKind.Related, "tok2"),
        };
        using var doc = Serialize(new ListResult(origin, matches));
        var list = doc.RootElement.GetProperty("matches");
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal("Servicio \"Uno\"", list[0].GetProperty("title").GetString());
        Assert.Equal("exact", list[0].GetProperty("kind").GetString());
        Assert.Equal("related", list[1].GetProperty("kind").GetString());
        Assert.DoesNotContain("password", doc.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://ejemplo.com", doc.RootElement.GetProperty("origin").GetString());
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("con \"comillas\" y \\barra\\")]
    [InlineData("salto\nde\r\nlínea\ty tab")]
    [InlineData("control\u0001\u001f fin")]
    [InlineData("ñandú €uro 日本語 😀")]
    [InlineData("</script><!-- & ' ` $")]
    [InlineData("\u2028 \u2029 separadores")]
    public void Secrets_survive_json_escaping_exactly(string password)
    {
        byte[] user = Encoding.UTF8.GetBytes("ana@ejemplo.com");
        byte[] pass = Encoding.UTF8.GetBytes(password);
        using var secret = new AutofillSecret(user, pass, includeUsername: true, includePassword: true);
        using var doc = Serialize(new SecretResult(secret));
        Assert.Equal(password, doc.RootElement.GetProperty("password").GetString());
        Assert.Equal("ana@ejemplo.com", doc.RootElement.GetProperty("username").GetString());
    }

    [Fact]
    public void Only_the_requested_parts_are_serialized()
    {
        byte[] user = "ana"u8.ToArray();
        byte[] pass = "Clave#Ficticia-1"u8.ToArray();
        using (var secret = new AutofillSecret(user, pass, includeUsername: true, includePassword: false))
        using (var doc = Serialize(new SecretResult(secret)))
        {
            Assert.True(doc.RootElement.TryGetProperty("username", out _));
            Assert.False(doc.RootElement.TryGetProperty("password", out _));
        }

        using (var secret = new AutofillSecret(user, pass, includeUsername: false, includePassword: true))
        using (var doc = Serialize(new SecretResult(secret)))
        {
            Assert.False(doc.RootElement.TryGetProperty("username", out _));
            Assert.True(doc.RootElement.TryGetProperty("password", out _));
        }
    }

    [Fact]
    public void Invalid_utf8_secrets_are_refused()
    {
        using var secret = new AutofillSecret("ana"u8, [0xFF, 0xFE], includeUsername: true, includePassword: true);
        using var result = new SecretResult(secret);
        Assert.Throws<InvalidOperationException>(() => result.Serialize());
    }

    [Fact]
    public void Secrets_are_zeroed_when_the_result_is_disposed()
    {
        var secret = new AutofillSecret("ana"u8, "Clave#Ficticia-1"u8, includeUsername: true, includePassword: true);
        var result = new SecretResult(secret);
        result.Dispose();
        Assert.True(secret.Password.IsEmpty);
        Assert.False(secret.HasPassword);
    }
}
