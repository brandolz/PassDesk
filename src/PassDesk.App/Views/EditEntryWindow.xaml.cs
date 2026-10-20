using System.Windows;
using PassDesk.App.Infrastructure;
using PassDesk.Auth;
using PassDesk.Core.Model;
using PassDesk.Core.Services;
using PassDesk.Crypto;

namespace PassDesk.App.Views;

public sealed record CategoryChoice(Category? Category, string Name);

/// <summary>
/// Alta y edición manual. Solo modifica la copia local de PassDesk y únicamente al pulsar «Guardar».
/// </summary>
public partial class EditEntryWindow : Window
{
    private readonly AppServices _services;
    private readonly Guid? _entryId;

    public EditEntryWindow(AppServices services, CredentialEntry? entry)
    {
        InitializeComponent();
        _services = services;
        _entryId = entry?.Id;
        WindowSecurity.Protect(this, services);
        SensitiveTextClipboard.Attach(PasswordVisible, services.Clipboard, ClipboardItemKind.Password, ShowCopied, () => ShowError("No se pudo acceder al portapapeles."));
        SensitiveTextClipboard.Attach(NotesBox, services.Clipboard, ClipboardItemKind.Note, ShowCopied, () => ShowError("No se pudo acceder al portapapeles."));

        var choices = new List<CategoryChoice> { new(null, "Automática (según el sitio)") };
        choices.AddRange(Enum.GetValues<Category>().Select(c => new CategoryChoice(c, CategoryInfo.Name(c))));
        CategoryBox.ItemsSource = choices;
        CategoryBox.SelectedIndex = 0;

        if (entry is not null)
        {
            Title = "Editar credencial";
            Heading.Text = "Editar credencial";
            TitleBox.Text = entry.Title;
            UrlBox.Text = entry.Url;
            UsernameBox.Text = entry.Username;
            // Para editarla, la contraseña tiene que pasar al control de la interfaz (acción explícita del usuario).
            PasswordInput.Password = entry.Password.Reveal();
            NotesBox.Text = entry.Notes.IsEmpty ? string.Empty : entry.Notes.Reveal();
            if (entry.IsCategoryManual)
            {
                CategoryBox.SelectedItem = choices.First(c => c.Category == entry.Category);
            }
        }
        else
        {
            Title = "Nueva credencial";
            InfoText.Text = "La credencial se guarda cifrada solo en PassDesk. No se añade a Contraseñas de Apple.";
        }

        services.Session.StateChanged += OnSessionStateChanged;
        Closed += (_, _) =>
        {
            services.Session.StateChanged -= OnSessionStateChanged;
            PasswordVisible.Clear();
            PasswordInput.Clear();
            NotesBox.Clear();
        };
        Loaded += (_, _) => TitleBox.Focus();
    }

    public Guid? SavedEntryId { get; private set; }

    private void OnSessionStateChanged(object? sender, VaultStateChangedEventArgs e)
    {
        if (e.NewState != VaultState.Unlocked)
        {
            Dispatcher.BeginInvoke(Close);
        }
    }

    private bool IsPasswordVisible => PasswordVisible.Visibility == Visibility.Visible;

    private void OnToggleReveal(object sender, RoutedEventArgs e)
    {
        if (IsPasswordVisible)
        {
            PasswordInput.Password = PasswordVisible.Text;
            PasswordVisible.Clear();
            PasswordVisible.Visibility = Visibility.Collapsed;
            PasswordInput.Visibility = Visibility.Visible;
            RevealButton.Content = "";
        }
        else
        {
            PasswordVisible.Text = PasswordInput.Password;
            PasswordInput.Visibility = Visibility.Collapsed;
            PasswordVisible.Visibility = Visibility.Visible;
            RevealButton.Content = "";
        }
    }

    private void OnGenerate(object sender, RoutedEventArgs e)
    {
        var generator = new GeneratorWindow(_services, pickMode: true) { Owner = this };
        if (generator.ShowDialog() == true && generator.SelectedPassword is { } generated)
        {
            if (IsPasswordVisible)
            {
                PasswordVisible.Text = generated;
            }
            else
            {
                PasswordInput.Password = generated;
            }
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string title = TitleBox.Text.Trim();
        string url = UrlBox.Text.Trim();
        if (title.Length == 0 && url.Length == 0)
        {
            ShowError("Indica al menos el nombre del servicio o su sitio web.");
            return;
        }

        ProtectedSecret password = IsPasswordVisible
            ? ProtectedSecret.FromString(PasswordVisible.Text)
            : FromPasswordBox();
        if (password.IsEmpty)
        {
            ShowError("La contraseña no puede estar vacía.");
            return;
        }

        var choice = CategoryBox.SelectedItem as CategoryChoice;
        var notes = ProtectedSecret.FromString(NotesBox.Text);
        var now = DateTimeOffset.UtcNow;
        if (title.Length == 0)
        {
            title = Core.Text.DomainUtil.GetDisplayHost(url);
        }

        try
        {
            if (_entryId is { } id)
            {
                bool found = _services.Session.Mutate(document =>
                {
                    var entry = document.Entries.FirstOrDefault(x => x.Id == id);
                    if (entry is null)
                    {
                        return false;
                    }

                    entry.Title = title;
                    entry.Url = url;
                    entry.Username = UsernameBox.Text;
                    entry.SetPassword(password);
                    entry.SetNotes(notes);
                    entry.IsCategoryManual = choice?.Category is not null;
                    entry.Category = choice?.Category ?? CategoryClassifier.Classify(title, url);
                    entry.ModifiedUtc = now;
                    return true;
                });
                if (!found)
                {
                    password.Dispose();
                    notes.Dispose();
                    ShowError("La credencial ya no existe.");
                    return;
                }

                SavedEntryId = id;
                Core.Diagnostics.AppLog.Info(Core.Diagnostics.LogEvent.EntryUpdated);
            }
            else
            {
                var entry = new CredentialEntry
                {
                    Title = title,
                    Url = url,
                    Username = UsernameBox.Text,
                    Category = choice?.Category ?? CategoryClassifier.Classify(title, url),
                    IsCategoryManual = choice?.Category is not null,
                    CreatedUtc = now,
                    ModifiedUtc = now,
                    Source = EntrySources.Manual,
                };
                entry.SetPassword(password);
                entry.SetNotes(notes);
                _services.Session.AddEntry(entry);
                SavedEntryId = entry.Id;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError("No se pudo guardar la bóveda cifrada.");
            return;
        }

        DialogResult = true;
    }

    private ProtectedSecret FromPasswordBox()
    {
        using var chars = SecureStringHelper.ToSensitiveChars(PasswordInput.SecurePassword);
        return ProtectedSecret.FromChars(chars.Span);
    }

    private void ShowCopied(TimeSpan delay) =>
        InfoText.Text = $"Copiado · se borrará del portapapeles en {delay.TotalSeconds:0} s.";

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
