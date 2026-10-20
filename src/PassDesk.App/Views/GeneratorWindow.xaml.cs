using System.Runtime.InteropServices;
using System.Windows;
using PassDesk.App.Infrastructure;
using PassDesk.Core.Security;
using PassDesk.Core.Services;

namespace PassDesk.App.Views;

/// <summary>
/// Generador opcional. En modo selección («Usar esta contraseña») devuelve la contraseña al editor;
/// solo se guarda si el usuario pulsa después «Guardar» en el editor.
/// </summary>
public partial class GeneratorWindow : Window
{
    private readonly AppServices _services;
    private bool _ready;

    public GeneratorWindow(AppServices services, bool pickMode)
    {
        InitializeComponent();
        _services = services;
        UseButton.Visibility = pickMode ? Visibility.Visible : Visibility.Collapsed;
        WindowSecurity.Protect(this, services);
        SensitiveTextClipboard.Attach(
            Output,
            services.Clipboard,
            ClipboardItemKind.GeneratedPassword,
            delay => StrengthText.Text = $"Copiada · se borrará del portapapeles en {delay.TotalSeconds:0} s",
            () => StrengthText.Text = "No se pudo acceder al portapapeles.");
        _ready = true;
        Generate();
        Closed += (_, _) => Output.Clear();
    }

    /// <summary>Contraseña elegida en modo selección.</summary>
    public string? SelectedPassword { get; private set; }

    private PasswordGeneratorOptions Options => new(
        Length: (int)LengthSlider.Value,
        Uppercase: UseUpper.IsChecked == true,
        Digits: UseDigits.IsChecked == true,
        Symbols: UseSymbols.IsChecked == true,
        ExcludeAmbiguous: AvoidAmbiguous.IsChecked == true);

    private void Generate()
    {
        if (!_ready)
        {
            return;
        }

        var options = Options;
        Output.Text = PasswordGenerator.Generate(options);
        double bits = PasswordGenerator.EstimateEntropyBits(options);
        string level = bits switch
        {
            < 60 => "Aceptable",
            < 80 => "Fuerte",
            _ => "Muy fuerte",
        };
        StrengthText.Text = $"Entropía estimada: {bits:0} bits · {level}";
    }

    private void OnOptionsChanged(object sender, RoutedEventArgs e) => Generate();

    private void OnRegenerate(object sender, RoutedEventArgs e) => Generate();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            var delay = _services.Clipboard.Copy(Output.Text, ClipboardItemKind.GeneratedPassword);
            StrengthText.Text = $"Copiada · se borrará del portapapeles en {delay.TotalSeconds:0} s";
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StrengthText.Text = "No se pudo acceder al portapapeles.";
        }
    }

    private void OnUse(object sender, RoutedEventArgs e)
    {
        SelectedPassword = Output.Text;
        DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
