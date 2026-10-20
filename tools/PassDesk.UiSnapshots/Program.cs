using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PassDesk.App;
using PassDesk.App.ViewModels;
using PassDesk.App.Views;
using PassDesk.Auth;
using PassDesk.Core.Model;
using PassDesk.Storage;

namespace PassDesk.UiSnapshots;

/// <summary>
/// Genera capturas de las ventanas de PassDesk con datos ficticios.
/// Uso: PassDesk.UiSnapshots &lt;carpeta-salida&gt; &lt;carpeta-perfil-nueva&gt; &lt;csv-ficticio&gt;
/// La carpeta de perfil debe no existir o estar vacía: la herramienta nunca toca datos reales.
/// </summary>
internal static class Program
{
    // Contraseña maestra ficticia, solo para el perfil temporal de la herramienta.
    private const string FakeMaster = "Captura-Ficticia#2026!demo";
    private const double Width = 1180;
    private const double Height = 760;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Uso: PassDesk.UiSnapshots <carpeta-salida> <carpeta-perfil-nueva> <csv-ficticio>");
            return 2;
        }

        string output = Path.GetFullPath(args[0]);
        string profile = Path.GetFullPath(args[1]);
        string csv = Path.GetFullPath(args[2]);
        if (Directory.Exists(profile) && Directory.EnumerateFileSystemEntries(profile).Any())
        {
            Console.Error.WriteLine("La carpeta de perfil debe estar vacía (nunca se usan datos reales).");
            return 2;
        }

        Directory.CreateDirectory(output);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), "{ \"QuickSearchEnabled\": false, \"ScreenCaptureProtection\": false, \"Theme\": \"Light\", \"DetectNewExports\": false }");

        // Se importa una copia: la importación borra el archivo de origen al terminar.
        string csvCopy = Path.Combine(profile, "Passwords.csv");
        File.Copy(csv, csvCopy);
        csv = csvCopy;
        Environment.SetEnvironmentVariable("PASSDESK_DATA_DIR", profile);

        // Application genérica con los recursos de PassDesk (sin ejecutar el arranque real de la app).
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PassDesk;component/Themes/Palette.Light.xaml") });
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PassDesk;component/Themes/Styles.xaml") });
        resources["BoolToVisibility"] = new BooleanToVisibilityConverter();
        resources["InverseBoolToVisibility"] = new PassDesk.App.Infrastructure.InverseBooleanToVisibilityConverter();
        app.Resources = resources;

        int exitCode = 0;
        var frame = new DispatcherFrame();
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await RunAsync(output, csv);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
            finally
            {
                frame.Continue = false;
            }
        });
        Dispatcher.PushFrame(frame);
        Console.Out.Flush();
        PassDesk.App.Infrastructure.NativeMethods.FinishProcessIfPdfEngineLoaded(exitCode);
        return exitCode;
    }

    private static async Task RunAsync(string output, string csv)
    {
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        using var services = AppServices.Create(paths, Dispatcher.CurrentDispatcher);
        services.Theme.Apply(ThemePreference.Light);

        var main = Offscreen(new MainWindow(services) { Width = Width, Height = Height });
        main.Show();
        await Idle();
        Save(main, output, "01-bienvenida-claro.png");

        if (!services.Session.CreateVault(FakeMaster).Succeeded)
        {
            throw new InvalidOperationException("No se pudo crear la bóveda de pruebas.");
        }

        await Idle();
        Save(main, output, "02-boveda-vacia-claro.png");

        // Importación con el asistente real.
        var import = Offscreen(new ImportWindow(services) { Owner = main });
        import.Show();
        await Idle();
        Save(import, output, "03-importar-origen-claro.png");
        await import.LoadFileAsync(csv);
        await Idle();
        Save(import, output, "04-importar-vista-previa-claro.png");
        import.PrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle();
        Save(import, output, "05-importar-resultado-claro.png");
        import.Close();

        var vm = (MainViewModel)main.DataContext;
        vm.SelectedEntry = vm.VisibleEntries.FirstOrDefault(e => e.Title == "Steam");
        await Idle(250);
        Save(main, output, "06-detalle-claro.png");

        vm.SearchText = "banco";
        await Idle();
        vm.Detail.ToggleReveal();
        await Idle();
        Save(main, output, "07-busqueda-revelada-claro.png");
        vm.Detail.Hide();
        vm.SearchText = string.Empty;

        vm.SelectedCategory = vm.Categories.First(c => c.Category == Category.Social);
        await Idle();
        Save(main, output, "08-categoria-claro.png");
        vm.SelectedCategory = vm.Categories[0];

        main.Width = 860;
        await Idle();
        Save(main, output, "09-ventana-estrecha-claro.png");
        main.Width = Width;

        // Avisos: exportación detectada y recordatorio de actualización.
        vm.SetDetectedExport(@"C:\Users\demo\Downloads\Passwords.zip");
        await Idle();
        Save(main, output, "17-aviso-exportacion-claro.png");
        vm.SetDetectedExport(null);
        services.Session.Mutate(document => document.LastImport = new ImportRecord(DateTimeOffset.UtcNow.AddDays(-35), "apple-export", 8, 0, 0));
        await Idle();
        Save(main, output, "18-recordatorio-claro.png");

        // Documentos cifrados con archivos ficticios generados aquí mismo.
        string folder = Path.GetDirectoryName(csv)!;
        string idCard = Path.Combine(folder, "DNI-ficticio.png");
        string pdf = Path.Combine(folder, "Contrato-ficticio.pdf");
        string text = Path.Combine(folder, "Notas-ficticias.txt");
        CreateFakeIdCard(idCard);
        File.WriteAllBytes(pdf, CreateFakePdf());
        File.WriteAllText(text, "Notas de prueba (ficticias)\nNúmero de póliza: 0000-TEST\nNo contiene datos reales.");

        vm.SelectedCategory = vm.Categories.First(c => c.IsDocuments);
        await Idle();
        Save(main, output, "19-documentos-vacio-claro.png");

        var added = services.Documents.AddFiles([idCard, pdf, text]);
        Console.WriteLine($"  Documentos añadidos: {added.Added}");
        await Idle();
        vm.SelectedDocument = vm.VisibleDocuments.First(d => d.Document.Kind == Core.Model.DocumentKind.Image);
        await Idle(500);
        Save(main, output, "20-documento-imagen-claro.png");
        vm.SelectedDocument = vm.VisibleDocuments.First(d => d.Document.Kind == Core.Model.DocumentKind.Pdf);
        await Idle(1500);
        Save(main, output, "21-documento-pdf-claro.png");
        vm.SelectedDocument = vm.VisibleDocuments.First(d => d.Document.Kind == Core.Model.DocumentKind.Text);
        await Idle(300);
        Save(main, output, "22-documento-texto-claro.png");
        vm.SelectedCategory = vm.Categories[0];

        // Tema oscuro
        services.Theme.Apply(ThemePreference.Dark);
        vm.SelectedEntry = vm.VisibleEntries.FirstOrDefault(e => e.Title == "Gmail");
        await Idle(250);
        Save(main, output, "10-detalle-oscuro.png");

        vm.SelectedCategory = vm.Categories.First(c => c.IsDocuments);
        vm.SelectedDocument = vm.VisibleDocuments.First(d => d.Document.Kind == Core.Model.DocumentKind.Image);
        await Idle(500);
        Save(main, output, "23-documento-imagen-oscuro.png");
        vm.SelectedCategory = vm.Categories[0];
        vm.SelectedEntry = vm.VisibleEntries.FirstOrDefault(e => e.Title == "Gmail");
        await Idle();

        var generator = Offscreen(new GeneratorWindow(services, pickMode: false) { Owner = main });
        generator.Show();
        await Idle();
        Save(generator, output, "11-generador-oscuro.png");
        generator.Close();

        var editor = Offscreen(new EditEntryWindow(services, vm.SelectedEntry!.Entry) { Owner = main });
        editor.Show();
        await Idle();
        Save(editor, output, "12-editar-oscuro.png");
        editor.Close();

        var settings = Offscreen(new SettingsWindow(services) { Owner = main });
        settings.Show();
        await Idle(300);
        Save(settings, output, "13-ajustes-oscuro.png");
        settings.Close();

        var quick = Offscreen(new QuickSearchWindow(services, main));
        quick.Show();
        quick.Query.Text = "a";
        await Idle();
        Save(quick, output, "14-busqueda-rapida-oscuro.png");
        quick.Close();

        services.LockNow(LockReason.Manual);
        await Idle(300);
        Save(main, output, "15-bloqueada-oscuro.png");

        services.Theme.Apply(ThemePreference.Light);
        await Idle();
        Save(main, output, "16-bloqueada-claro.png");

        main.Close();
        Console.WriteLine($"Capturas generadas en {output}");
    }

    /// <summary>Tarjeta de identidad inventada (sin ningún dato real) para probar el visor de imágenes.</summary>
    private static void CreateFakeIdCard(string path)
    {
        const int width = 856, height = 540;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = new LinearGradientBrush(Color.FromRgb(0xDD, 0xEB, 0xF7), Color.FromRgb(0xF7, 0xE8, 0xD8), 20);
            dc.DrawRoundedRectangle(background, new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA8, 0xB8)), 3), new Rect(4, 4, width - 8, height - 8), 28, 28);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xC9, 0xD3, 0xDE)), null, new Rect(48, 140, 210, 260), 14, 14);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xA9, 0xB6, 0xC4)), null, new Point(153, 235), 52, 52);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xA9, 0xB6, 0xC4)), null, new Rect(83, 300, 140, 90), 40, 40);
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            void Text(string value, double size, double x, double y, Brush brush) =>
                dc.DrawText(new FormattedText(value, System.Globalization.CultureInfo.GetCultureInfo("es-ES"), FlowDirection.LeftToRight, typeface, size, brush, 1.0), new Point(x, y));
            var dark = new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x37));
            var muted = new SolidColorBrush(Color.FromRgb(0x5A, 0x67, 0x78));
            Text("DOCUMENTO DE PRUEBA · FICTICIO", 30, 48, 48, dark);
            Text("Solo para probar el visor de PassDesk", 18, 48, 92, muted);
            Text("Nombre", 16, 300, 150, muted);
            Text("Persona de Prueba", 28, 300, 172, dark);
            Text("Número", 16, 300, 232, muted);
            Text("00000000T", 28, 300, 254, dark);
            Text("Válido hasta", 16, 300, 314, muted);
            Text("01/01/2099", 28, 300, 336, dark);
            Text("SIN VALIDEZ LEGAL", 22, 560, 470, new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>PDF mínimo válido de una página, generado a mano, con texto ficticio.</summary>
    private static byte[] CreateFakePdf()
    {
        string content = "BT /F1 26 Tf 72 760 Td (Contrato de prueba - FICTICIO) Tj ET\n" +
                         "BT /F1 13 Tf 72 726 Td (Documento generado para probar el visor PDF de PassDesk.) Tj ET\n" +
                         "BT /F1 13 Tf 72 704 Td (No contiene datos reales.) Tj ET\n";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];

        var builder = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = builder.Length;
        builder.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            builder.Append($"{offset:D10} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static T Offscreen<T>(T window)
        where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        return window;
    }

    private static async Task Idle(int extraMilliseconds = 120)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(extraMilliseconds);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window window, string folder, string name)
    {
        window.UpdateLayout();

        // Raíz visual del área cliente: incluye el fondo y los márgenes del contenido.
        var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        int width = (int)Math.Ceiling(root.ActualWidth);
        int height = (int)Math.Ceiling(root.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
        }

        bitmap.Render(visual);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
        Console.WriteLine("  " + name);
    }
}
