using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PassDesk.Browser.Broker;
using PassDesk.Browser.Host;
using PassDesk.Browser.Ipc;
using PassDesk.Browser.Protocol;

namespace PassDesk.Browser.Tests;

/// <summary>Servidor real de tubería con una bóveda ficticia, para probar la comunicación de extremo a extremo.</summary>
internal sealed class PipeFixture : IAsyncDisposable
{
    public const string Extension = "chrome-extension://abcdefghijklmnopabcdefghijklmnop/";

    public PipeFixture(
        bool requireConfirmation = false,
        IPipeClientVerifier? verifier = null,
        PipeServerLimits? limits = null,
        ScriptedConfirmer? confirmer = null)
    {
        Vault = new FakeVault();
        Vault.Add("Ejemplo", "https://ejemplo.test", Fake.User, Fake.Password);
        PipeName = "PassDeskTest." + Guid.NewGuid().ToString("N");
        Audit = new RecordingAudit();
        Broker = new AutofillBroker(
            Vault,
            () => new AutofillPolicy { RequireConfirmation = requireConfirmation },
            TimeProvider.System,
            confirmer,
            new RecordingUnlock(),
            Audit);
        Server = new AutofillPipeServer(PipeName, Broker, verifier, Audit, limits);
        Server.Start();
    }

    public FakeVault Vault { get; }

    public string PipeName { get; }

    public RecordingAudit Audit { get; }

    public AutofillBroker Broker { get; }

    public AutofillPipeServer Server { get; }

    public NativeHostOptions HostOptions(TimeSpan? responseTimeout = null) => new()
    {
        AllowedCallers = [Extension],
        PipeName = PipeName,
        ResponseTimeout = responseTimeout ?? TimeSpan.FromSeconds(10),
    };

    public async Task<NamedPipeClientStream> ConnectAsync()
    {
        var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        Broker.Dispose();
    }

    public static byte[] Frame(string json) => Frame(Encoding.UTF8.GetBytes(json));

    public static byte[] Frame(byte[] payload)
    {
        byte[] result = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)payload.Length);
        payload.CopyTo(result, 4);
        return result;
    }

    public static async Task<JsonDocument?> ReadReplyAsync(Stream stream, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var (status, buffer) = await NativeMessageFraming.ReadAsync(stream, 1 << 20, cts.Token);
        if (status != FrameStatus.Ok)
        {
            return null;
        }

        using (buffer)
        {
            return JsonDocument.Parse(buffer!.Span.ToArray());
        }
    }

    public async Task<JsonDocument> RequestAsync(string json)
    {
        await using var client = await ConnectAsync();
        await client.WriteAsync(Frame(json));
        return (await ReadReplyAsync(client))!;
    }
}

public class PipeServerTests
{
    [Fact]
    public async Task Status_and_list_work_over_a_real_pipe()
    {
        await using var fixture = new PipeFixture();
        using var status = await fixture.RequestAsync("""{"v":1,"type":"status","rid":"s1"}""");
        Assert.True(status.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("ready", status.RootElement.GetProperty("state").GetString());
        Assert.Equal("s1", status.RootElement.GetProperty("rid").GetString());

        using var list = await fixture.RequestAsync("""{"v":1,"type":"list","origin":"https://ejemplo.test"}""");
        var match = Assert.Single(list.RootElement.GetProperty("matches").EnumerateArray());
        Assert.Equal(Fake.User, match.GetProperty("username").GetString());
    }

    [Fact]
    public async Task A_full_list_then_fill_round_trip_returns_the_secret_only_on_fill()
    {
        await using var fixture = new PipeFixture();
        using var list = await fixture.RequestAsync("""{"v":1,"type":"list","origin":"https://ejemplo.test"}""");
        Assert.DoesNotContain(Fake.Password, list.RootElement.GetRawText());
        var match = list.RootElement.GetProperty("matches")[0];
        string fill = $$"""{"v":1,"type":"fill","origin":"https://ejemplo.test","id":"{{match.GetProperty("id").GetString()}}","token":"{{match.GetProperty("token").GetString()}}","scope":"both"}""";
        using var reply = await fixture.RequestAsync(fill);
        Assert.Equal(Fake.Password, reply.RootElement.GetProperty("password").GetString());
        Assert.Equal(Fake.User, reply.RootElement.GetProperty("username").GetString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"v":1,"type":"dump"}""")]
    [InlineData("""{"v":1,"type":"status","extra":true}""")]
    [InlineData("""{"v":1,"type":"list","origin":"https://ejemplo.test/ruta"}""")]
    public async Task Malformed_requests_get_a_generic_error(string json)
    {
        await using var fixture = new PipeFixture();
        using var reply = await fixture.RequestAsync(json);
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.BadRequest, reply.RootElement.GetProperty("error").GetString());
        Assert.True(fixture.Audit.Has(AutofillAuditEvent.BadRequest));
    }

    [Fact]
    public async Task Internal_pages_are_reported_as_unsupported_origins()
    {
        await using var fixture = new PipeFixture();
        using var reply = await fixture.RequestAsync("""{"v":1,"type":"list","origin":"chrome://settings"}""");
        Assert.Equal(ErrorCodes.UnsupportedOrigin, reply.RootElement.GetProperty("error").GetString());
        using var ext = await fixture.RequestAsync("""{"v":1,"type":"list","origin":"chrome-extension://abcdefghijklmnopabcdefghijklmnop"}""");
        Assert.Equal(ErrorCodes.UnsupportedOrigin, ext.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Oversized_requests_are_refused_without_being_read()
    {
        await using var fixture = new PipeFixture();
        await using var client = await fixture.ConnectAsync();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 10 * 1024 * 1024);
        await client.WriteAsync(header);
        using var reply = await PipeFixture.ReadReplyAsync(client);
        Assert.Equal(ErrorCodes.TooLarge, reply!.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_client_that_sends_nothing_is_dropped_by_the_timeout()
    {
        await using var fixture = new PipeFixture(limits: new PipeServerLimits { RequestReadTimeout = TimeSpan.FromMilliseconds(200) });
        await using var client = await fixture.ConnectAsync();
        // Sin enviar nada: el servidor cierra la conexión.
        using var reply = await PipeFixture.ReadReplyAsync(client, timeoutMs: 3000);
        Assert.Null(reply);

        // Y sigue atendiendo a los demás.
        using var status = await fixture.RequestAsync("""{"v":1,"type":"status"}""");
        Assert.True(status.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task A_half_sent_request_is_dropped_by_the_timeout()
    {
        await using var fixture = new PipeFixture(limits: new PipeServerLimits { RequestReadTimeout = TimeSpan.FromMilliseconds(200) });
        await using var client = await fixture.ConnectAsync();
        byte[] partial = PipeFixture.Frame("""{"v":1,"type":"status"}""")[..10];
        await client.WriteAsync(partial);
        Assert.Null(await PipeFixture.ReadReplyAsync(client, timeoutMs: 3000));
    }

    [Fact]
    public async Task Too_many_simultaneous_connections_are_told_to_retry()
    {
        await using var fixture = new PipeFixture(limits: new PipeServerLimits { MaxConcurrentConnections = 2, RequestReadTimeout = TimeSpan.FromSeconds(10) });
        await using var a = await fixture.ConnectAsync();
        await using var b = await fixture.ConnectAsync();
        await Task.Delay(200);
        await using var c = await fixture.ConnectAsync();
        using var reply = await PipeFixture.ReadReplyAsync(c);
        Assert.Equal(ErrorCodes.Busy, reply!.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_unverified_client_gets_nothing()
    {
        await using var fixture = new PipeFixture(verifier: new RejectAll());
        // El servidor responde sin leer la solicitud, así que el cliente solo escucha.
        await using var client = await fixture.ConnectAsync();
        using var reply = await PipeFixture.ReadReplyAsync(client);
        Assert.Equal(ErrorCodes.UnauthorizedCaller, reply!.RootElement.GetProperty("error").GetString());
        Assert.True(fixture.Audit.Has(AutofillAuditEvent.ClientRejected));
    }

    [Fact]
    public async Task Locking_while_a_request_waits_for_confirmation_never_sends_the_password_down_the_pipe()
    {
        var confirmer = new ScriptedConfirmer { HoldAnswer = true };
        await using var fixture = new PipeFixture(requireConfirmation: true, confirmer: confirmer);
        using var list = await fixture.RequestAsync("""{"v":1,"type":"list","origin":"https://ejemplo.test"}""");
        var match = list.RootElement.GetProperty("matches")[0];
        string fill = $$"""{"v":1,"type":"fill","origin":"https://ejemplo.test","id":"{{match.GetProperty("id").GetString()}}","token":"{{match.GetProperty("token").GetString()}}","scope":"both"}""";

        await using var client = await fixture.ConnectAsync();
        await client.WriteAsync(PipeFixture.Frame(fill));
        await confirmer.Shown_.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Vault.Lock();
        confirmer.Complete(true);

        using var reply = await PipeFixture.ReadReplyAsync(client);
        Assert.Equal(ErrorCodes.Locked, reply!.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(Fake.Password, reply.RootElement.GetRawText());
    }

    [Fact]
    public async Task Concurrent_clients_are_all_served()
    {
        await using var fixture = new PipeFixture();
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            using var reply = await fixture.RequestAsync("""{"v":1,"type":"status"}""");
            return reply.RootElement.GetProperty("ok").GetBoolean();
        });
        Assert.All(await Task.WhenAll(tasks), ok => Assert.True(ok));
    }

    [Fact]
    public async Task The_server_keeps_working_after_a_client_disconnects_mid_request()
    {
        await using var fixture = new PipeFixture();
        for (int i = 0; i < 5; i++)
        {
            var client = await fixture.ConnectAsync();
            await client.WriteAsync(PipeFixture.Frame("""{"v":1,"type":"status"}""")[..6]);
            await client.DisposeAsync();
        }

        using var status = await fixture.RequestAsync("""{"v":1,"type":"status"}""");
        Assert.True(status.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Pipe_names_depend_on_the_data_folder_and_are_stable()
    {
        string a = PipeNames.ForDataRoot(Path.GetFullPath("/tmp/passdesk-a"));
        string b = PipeNames.ForDataRoot(Path.GetFullPath("/tmp/passdesk-b"));
        Assert.NotEqual(a, b);
        Assert.Equal(a, PipeNames.ForDataRoot(Path.GetFullPath("/tmp/passdesk-a")));
        Assert.StartsWith("PassDesk.Autofill.", a);
        Assert.Matches("^PassDesk\\.Autofill\\.[0-9A-F]{16}$", a);
    }

    private sealed class RejectAll : IPipeClientVerifier
    {
        public bool Verify(NamedPipeServerStream pipe) => false;
    }
}

public class NativeHostTests
{
    private static async Task<(int Exit, JsonDocument? Reply, byte[] Raw)> RunHost(
        NativeHostOptions options,
        string[] args,
        byte[] stdin)
    {
        using var input = new MemoryStream(stdin);
        using var output = new MemoryStream();
        int exit = await NativeHostRunner.RunAsync(args, input, output, options, CancellationToken.None);
        byte[] raw = output.ToArray();
        if (raw.Length < 4)
        {
            return (exit, null, raw);
        }

        return (exit, JsonDocument.Parse(raw.AsMemory(4)), raw);
    }

    [Fact]
    public async Task Relays_a_valid_request_to_PassDesk_and_returns_its_reply_framed()
    {
        await using var fixture = new PipeFixture();
        var (exit, reply, _) = await RunHost(fixture.HostOptions(), [PipeFixture.Extension, "--parent-window=0"], PipeFixture.Frame("""{"v":1,"type":"list","origin":"https://ejemplo.test"}"""));
        using (reply)
        {
            Assert.Equal(NativeHostRunner.ExitOk, exit);
            Assert.Equal(Fake.User, reply!.RootElement.GetProperty("matches")[0].GetProperty("username").GetString());
        }
    }

    [Theory]
    [InlineData("chrome-extension://ponmlkjihgfedcbaponmlkjihgfedcba/")]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop")]
    [InlineData("CHROME-EXTENSION://abcdefghijklmnopabcdefghijklmnop/")]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop/ ")]
    [InlineData("https://ejemplo.test/")]
    [InlineData("")]
    public async Task Requests_from_other_callers_are_not_even_read(string caller)
    {
        await using var fixture = new PipeFixture();
        var stdin = new TrackingStream(PipeFixture.Frame("""{"v":1,"type":"list","origin":"https://ejemplo.test"}"""));
        using var output = new MemoryStream();
        int exit = await NativeHostRunner.RunAsync([caller], stdin, output, fixture.HostOptions(), CancellationToken.None);
        Assert.Equal(NativeHostRunner.ExitUnauthorizedCaller, exit);
        Assert.Equal(0, stdin.BytesRead);
        Assert.Equal(0, output.Length);
        Assert.False(fixture.Audit.Has(AutofillAuditEvent.Listed));
    }

    [Fact]
    public async Task Missing_caller_argument_is_rejected()
    {
        await using var fixture = new PipeFixture();
        var (exit, _, raw) = await RunHost(fixture.HostOptions(), [], PipeFixture.Frame("""{"v":1,"type":"status"}"""));
        Assert.Equal(NativeHostRunner.ExitUnauthorizedCaller, exit);
        Assert.Empty(raw);
    }

    [Fact]
    public async Task With_no_authorized_extensions_nobody_is_served()
    {
        await using var fixture = new PipeFixture();
        var options = fixture.HostOptions() with { AllowedCallers = [] };
        var (exit, _, raw) = await RunHost(options, [PipeFixture.Extension], PipeFixture.Frame("""{"v":1,"type":"status"}"""));
        Assert.Equal(NativeHostRunner.ExitUnauthorizedCaller, exit);
        Assert.Empty(raw);
    }

    [Theory]
    [InlineData("not json", ErrorCodes.BadRequest)]
    [InlineData("""{"v":1,"type":"export"}""", ErrorCodes.BadRequest)]
    [InlineData("""{"v":1,"type":"list","origin":"edge://settings"}""", ErrorCodes.UnsupportedOrigin)]
    public async Task Malformed_messages_are_stopped_in_the_host_and_never_reach_PassDesk(string json, string expected)
    {
        await using var fixture = new PipeFixture();
        var (exit, reply, _) = await RunHost(fixture.HostOptions(), [PipeFixture.Extension], PipeFixture.Frame(json));
        using (reply)
        {
            Assert.Equal(NativeHostRunner.ExitBadInput, exit);
            Assert.Equal(expected, reply!.RootElement.GetProperty("error").GetString());
        }

        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task Oversized_messages_are_refused_without_reading_them()
    {
        await using var fixture = new PipeFixture();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 100 * 1024 * 1024);
        var stdin = new TrackingStream([.. header, .. new byte[64]]);
        using var output = new MemoryStream();
        int exit = await NativeHostRunner.RunAsync([PipeFixture.Extension], stdin, output, fixture.HostOptions(), CancellationToken.None);
        Assert.Equal(NativeHostRunner.ExitBadInput, exit);
        Assert.Equal(4, stdin.BytesRead);
        using var reply = JsonDocument.Parse(output.ToArray().AsMemory(4));
        Assert.Equal(ErrorCodes.TooLarge, reply.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Empty_or_truncated_input_ends_quietly()
    {
        await using var fixture = new PipeFixture();
        var (emptyExit, _, emptyRaw) = await RunHost(fixture.HostOptions(), [PipeFixture.Extension], []);
        Assert.Equal(NativeHostRunner.ExitBadInput, emptyExit);
        Assert.Empty(emptyRaw);
        var (cutExit, _, cutRaw) = await RunHost(fixture.HostOptions(), [PipeFixture.Extension], PipeFixture.Frame("""{"v":1,"type":"status"}""")[..9]);
        Assert.Equal(NativeHostRunner.ExitBadInput, cutExit);
        Assert.Empty(cutRaw);
    }

    [Fact]
    public async Task When_PassDesk_is_not_running_the_reply_is_unavailable()
    {
        var options = new NativeHostOptions
        {
            AllowedCallers = [PipeFixture.Extension],
            PipeName = "PassDeskTest.nadie." + Guid.NewGuid().ToString("N"),
            ConnectTimeout = TimeSpan.FromMilliseconds(300),
        };
        var (exit, reply, _) = await RunHost(options, [PipeFixture.Extension], PipeFixture.Frame("""{"v":1,"type":"status"}"""));
        using (reply)
        {
            Assert.Equal(NativeHostRunner.ExitOk, exit);
            Assert.False(reply!.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(ErrorCodes.Unavailable, reply.RootElement.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task A_PassDesk_that_never_answers_ends_with_unavailable()
    {
        string pipe = "PassDeskTest.mudo." + Guid.NewGuid().ToString("N");
        await using var silent = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accept = silent.WaitForConnectionAsync();
        var options = new NativeHostOptions
        {
            AllowedCallers = [PipeFixture.Extension],
            PipeName = pipe,
            ResponseTimeout = TimeSpan.FromMilliseconds(300),
        };
        var (exit, reply, _) = await RunHost(options, [PipeFixture.Extension], PipeFixture.Frame("""{"v":1,"type":"status"}"""));
        using (reply)
        {
            Assert.Equal(NativeHostRunner.ExitOk, exit);
            Assert.Equal(ErrorCodes.Unavailable, reply!.RootElement.GetProperty("error").GetString());
        }

        await accept;
    }

    [Fact]
    public async Task A_reply_that_is_not_a_json_object_or_is_too_large_is_replaced_by_a_generic_error()
    {
        string pipe = "PassDeskTest.raro." + Guid.NewGuid().ToString("N");
        foreach (byte[] payload in new[] { "basura"u8.ToArray(), new byte[NativeHostRunner.MaxResponseBytes + 1] })
        {
            await using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var serve = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                var (_, request) = await NativeMessageFraming.ReadAsync(server, 4096, CancellationToken.None);
                request?.Dispose();
                await server.WriteAsync(PipeFixture.Frame(payload));
                await server.FlushAsync();
            });
            var options = new NativeHostOptions { AllowedCallers = [PipeFixture.Extension], PipeName = pipe };
            var (_, reply, _) = await RunHost(options, [PipeFixture.Extension], PipeFixture.Frame("""{"v":1,"type":"status"}"""));
            using (reply)
            {
                Assert.Equal(ErrorCodes.Internal, reply!.RootElement.GetProperty("error").GetString());
            }

            await serve;
        }
    }

    [Fact]
    public void Allowed_caller_lists_keep_only_exact_extension_origins()
    {
        var parsed = NativeHostRunner.ParseAllowedCallers(
        [
            "  chrome-extension://abcdefghijklmnopabcdefghijklmnop/  ",
            "chrome-extension://abcdefghijklmnopabcdefghijklmnop/",
            "chrome-extension://ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP/",
            "chrome-extension://abcdefghijklmnopabcdefghijklmno/",
            "chrome-extension://abcdefghijklmnopabcdefghijklmnop/ruta",
            "chrome-extension://*/",
            "https://ejemplo.test/",
            "",
            "# comentario",
        ]);
        Assert.Equal(["chrome-extension://abcdefghijklmnopabcdefghijklmnop/"], parsed);
    }

    private sealed class TrackingStream(byte[] data) : MemoryStream(data)
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
