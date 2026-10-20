using System.Windows;
using PassDesk.App.Infrastructure;

namespace PassDesk.App.Views;

/// <summary>Diálogo sencillo para pedir un texto corto (por ejemplo, el nombre de un documento).</summary>
public partial class TextPromptWindow : Window
{
    public TextPromptWindow(AppServices services, string heading, string label, string initialValue)
    {
        InitializeComponent();
        WindowSecurity.Protect(this, services);
        Heading.Text = heading;
        FieldLabel.Text = label;
        Input.Text = initialValue;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public string Value => Input.Text.Trim();

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (Value.Length > 0)
        {
            DialogResult = true;
        }
    }
}
