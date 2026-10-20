using System.IO.Compression;
using System.Text;
using PassDesk.Import;

namespace PassDesk.Tests;

public class ExportDetectionTests
{
    private const string AppleCsv =
        "Title,URL,Username,Password,Notes,OTPAuth\r\n" +
        "Ejemplo,https://ejemplo.example,usuario,Clave-Ficticia-1,,\r\n";

    private const string BrowserCsv = "name,url,username,password\nEjemplo,https://ejemplo.example,u,Clave-Ficticia-2\n";

    [Fact]
    public void Recognizes_apple_and_browser_csv_exports()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.File("Passwords.csv"), AppleCsv, new UTF8Encoding(true));
        File.WriteAllText(temp.File("chrome.csv"), BrowserCsv);
        File.WriteAllText(temp.File("utf16.csv"), AppleCsv, Encoding.Unicode);
        File.WriteAllText(temp.File("utf16be.csv"), AppleCsv, Encoding.BigEndianUnicode);

        Assert.True(ExportFileProbe.LooksLikePasswordExport(temp.File("Passwords.csv")));
        Assert.True(ExportFileProbe.LooksLikePasswordExport(temp.File("chrome.csv")));
        Assert.True(ExportFileProbe.LooksLikePasswordExport(temp.File("utf16.csv")));
        Assert.True(ExportFileProbe.LooksLikePasswordExport(temp.File("utf16be.csv")));
    }

    [Fact]
    public void Ignores_unrelated_files()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.File("gastos.csv"), "Fecha,Concepto,Importe\n2026-10-01,Café,2.50\n");
        File.WriteAllText(temp.File("vacio.csv"), string.Empty);
        File.WriteAllText(temp.File("Passwords.txt"), AppleCsv);
        File.WriteAllBytes(temp.File("roto.zip"), [0x50, 0x4B, 0x03, 0x04, 9, 9, 9]);

        Assert.False(ExportFileProbe.LooksLikePasswordExport(temp.File("gastos.csv")));
        Assert.False(ExportFileProbe.LooksLikePasswordExport(temp.File("vacio.csv")));
        Assert.False(ExportFileProbe.LooksLikePasswordExport(temp.File("Passwords.txt")));
        Assert.False(ExportFileProbe.LooksLikePasswordExport(temp.File("roto.zip")));
        Assert.False(ExportFileProbe.LooksLikePasswordExport(temp.File("no-existe.csv")));
    }

    [Fact]
    public void Recognizes_safari_zip_exports_by_name_or_header()
    {
        using var temp = new TempDirectory();
        CreateZip(temp.File("Safari.zip"), ("Bookmarks.html", "<html></html>"), ("Passwords.csv", AppleCsv));
        CreateZip(temp.File("generico.zip"), ("datos.csv", BrowserCsv));
        CreateZip(temp.File("fotos.zip"), ("lista.csv", "a,b,c\n1,2,3\n"), ("nota.txt", "hola"));

        Assert.True(ExportFileProbe.LooksLikePasswordExport(temp.File("Safari.zip")));
        Assert.True(ExportFileProbe.LooksLikePasswordExport(temp.File("generico.zip")));
        Assert.False(ExportFileProbe.LooksLikePasswordExport(temp.File("fotos.zip")));
    }

    [Theory]
    [InlineData("Title,URL,Username,Password,Notes,OTPAuth", true)]
    [InlineData("\"name\",\"url\",\"username\",\"password\"", true)]
    [InlineData("Nombre;Sitio web;Usuario;Contraseña", true)]
    [InlineData("Title,URL,Username", false)]
    [InlineData("password", false)]
    [InlineData("", false)]
    public void Header_recognition_matches_import_rules(string header, bool expected)
    {
        Assert.Equal(expected, ExportFileReader.IsRecognizedHeader(header));
    }

    [Fact]
    public async Task Watcher_reports_new_exports_once_and_ignores_other_files()
    {
        using var temp = new TempDirectory();
        using var watcher = new ExportFolderWatcher([temp.Path], TimeSpan.FromMilliseconds(200));
        var detected = new List<string>();
        var signal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.ExportDetected += (_, e) =>
        {
            lock (detected)
            {
                detected.Add(e.Path);
            }

            signal.TrySetResult(e.Path);
        };
        watcher.Enabled = true;

        File.WriteAllText(temp.File("gastos.csv"), "Fecha,Concepto,Importe\n");
        File.WriteAllText(temp.File("Passwords.csv"), AppleCsv);

        var first = await signal.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(temp.File("Passwords.csv"), first, ignoreCase: true);

        await Task.Delay(1500);
        lock (detected)
        {
            Assert.Single(detected);
        }
    }

    [Fact]
    public async Task Startup_scan_finds_recent_exports_but_not_handled_ones()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.File("Passwords.csv"), AppleCsv);
        File.WriteAllText(temp.File("antigua.csv"), BrowserCsv);
        File.SetLastWriteTimeUtc(temp.File("antigua.csv"), DateTime.UtcNow.AddDays(-3));

        using var watcher = new ExportFolderWatcher([temp.Path], TimeSpan.FromMilliseconds(100));
        var signal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detected = new List<string>();
        watcher.ExportDetected += (_, e) =>
        {
            lock (detected)
            {
                detected.Add(e.Path);
            }

            signal.TrySetResult(e.Path);
        };

        watcher.ScanRecent(TimeSpan.FromHours(24));
        Assert.Equal(temp.File("Passwords.csv"), await signal.Task.WaitAsync(TimeSpan.FromSeconds(10)), ignoreCase: true);

        await Task.Delay(800);
        watcher.MarkHandled(temp.File("Passwords.csv"));
        watcher.ScanRecent(TimeSpan.FromHours(24));
        await Task.Delay(1200);
        lock (detected)
        {
            Assert.Single(detected);
        }
    }

    private static void CreateZip(string path, params (string Name, string Content)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }
}
