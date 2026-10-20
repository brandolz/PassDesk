using PassDesk.Auth;
using PassDesk.Core.Diagnostics;
using PassDesk.Core.Services;
using PassDesk.Crypto;
using PassDesk.Import;
using PassDesk.Storage;

namespace PassDesk.Tests;

/// <summary>Verifica que ningún flujo escribe secretos, usuarios ni URL en los registros.</summary>
public class LoggingTests
{
    private const string LogSecret = "Registro#Secreto-Ficticio-7731";
    private const string LogUser = "usuario.registro.ficticio@example.com";
    private const string LogHost = "sitio-registro-ficticio.example";

    [Fact]
    public async Task No_secrets_reach_memory_or_file_logs_in_any_flow()
    {
        using var temp = new TempDirectory();
        var memory = new MemoryLogSink();
        var file = new FileLogSink(temp.File("logs"));
        AppLog.AddSink(memory);
        AppLog.AddSink(file);
        try
        {
            // Crear bóveda, añadir, bloquear, fallar y desbloquear.
            var session = new VaultSession(new DpapiVaultStore(temp.File("vault.pdv")), new InMemorySecurityStateStore(), new FakeClock(), Fake.FastIterations);
            session.CreateVault(Fake.MasterPassword);
            session.AddEntry(Fake.Entry("Servicio", $"https://{LogHost}", LogUser, LogSecret, notes: LogSecret));
            session.Lock(LockReason.Manual);
            session.Unlock("contraseña-incorrecta-ficticia");
            session.Unlock(Fake.MasterPassword);
            await session.QuickUnlockAsync(new FakePresenceVerifier(PresenceResult.Verified), "x");
            session.ChangeMasterPassword(Fake.MasterPassword, Fake.OtherMasterPassword);

            // Importación válida e inválida.
            string csvPath = temp.File("Passwords.csv");
            File.WriteAllText(csvPath, $"Title,URL,Username,Password\r\nServicio,https://{LogHost},{LogUser},{LogSecret}\r\n");
            using (var parsed = ExportFileReader.ReadFile(csvPath))
            {
                session.Mutate(doc => ImportApplier.Apply(doc.Entries, ImportAnalyzer.Analyze(doc.Entries, parsed), ConflictResolution.KeepBoth, DateTimeOffset.UtcNow, "apple-export"));
            }

            string badPath = temp.File("roto.csv");
            File.WriteAllText(badPath, $"Title,URL,Username,Password\r\nA,https://{LogHost},{LogUser},\"{LogSecret}\r\n");
            Assert.Throws<ImportFileException>(() => ExportFileReader.ReadFile(badPath));
            SecureFileDeleter.Delete(csvPath);

            // Portapapeles.
            var clipboard = new FakeClipboard();
            var scheduler = new ManualScheduler();
            using (var guard = new ClipboardGuard(clipboard, scheduler, () => TimeSpan.FromSeconds(10)))
            {
                guard.Copy(LogSecret, ClipboardItemKind.Password);
                scheduler.FireAll();
            }

            // Excepción cuyo mensaje contiene un secreto: solo se registra su tipo.
            AppLog.Error(LogEvent.UnhandledException, new InvalidOperationException($"fallo con {LogSecret} de {LogUser}"));

            session.Dispose();
        }
        finally
        {
            AppLog.RemoveSink(memory);
            AppLog.RemoveSink(file);
        }

        string fileText = File.ReadAllText(file.FilePath);
        foreach (string text in new[] { memory.AllText, fileText })
        {
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain(LogSecret, text);
            Assert.DoesNotContain(LogUser, text);
            Assert.DoesNotContain(LogHost, text);
            Assert.DoesNotContain(Fake.MasterPassword, text);
            Assert.DoesNotContain(Fake.OtherMasterPassword, text);
        }

        Assert.Contains("VaultUnlockFailed", fileText);
        Assert.Contains("exception=System.InvalidOperationException", fileText);
    }

    [Fact]
    public void Metric_names_are_sanitized()
    {
        var memory = new MemoryLogSink();
        AppLog.AddSink(memory);
        try
        {
            AppLog.Info(LogEvent.ImportApplied, new LogMetric("añadidos " + LogSecret, 3));
        }
        finally
        {
            AppLog.RemoveSink(memory);
        }

        string text = memory.AllText;
        Assert.DoesNotContain(LogSecret, text);
        Assert.Contains("=3", text);
    }

    [Fact]
    public void Exceptions_never_expose_file_content()
    {
        var ex = Assert.Throws<ImportFileException>(() => ExportFileReader.ParseText($"Title,URL,Username,Password\r\nA,b,c,\"{LogSecret}", "Prueba"));
        Assert.DoesNotContain(LogSecret, ex.Message);
        Assert.DoesNotContain(LogSecret, ex.ToString());
    }
}
