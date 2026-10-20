using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PassDesk.App.Infrastructure;
using PassDesk.App.ViewModels;
using PassDesk.Auth;
using PassDesk.Core.Search;
using PassDesk.Core.Services;
using PassDesk.Core.Text;

namespace PassDesk.App.Views;

/// <summary>
/// Ventana compacta de consulta que se abre con el atajo global. Copia credenciales con una sola
/// tecla y se oculta al perder el foco (sin dejar nada visible en pantalla).
/// </summary>
public partial class QuickSearchWindow : Window
{
    private const int MaxResults = 8;
    private readonly AppServices _services;
    private readonly MainWindow _main;
    private bool _busy;

    public QuickSearchWindow(AppServices services, MainWindow main)
    {
        InitializeComponent();
        _services = services;
        _main = main;
        WindowSecurity.Protect(this, services, roundCorners: true);
        Deactivated += (_, _) =>
        {
            // Durante Windows Hello el diálogo del sistema toma el foco: no se oculta.
            if (!_busy)
            {
                HideWindow();
            }
        };

        // Alt+F4 (o WM_CLOSE) solo la oculta, como Esc: la ventana principal la reutiliza y una ventana cerrada no se
        // puede volver a mostrar. Al salir de la aplicación, WPF la cierra aunque se cancele.
        Closing += (_, e) =>
        {
            e.Cancel = true;
            HideWindow();
        };
        services.Session.StateChanged += (_, _) => Dispatcher.BeginInvoke(RefreshState);
    }

    public void ShowCentered()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + ((area.Width - Width) / 2);
        Top = area.Top + (area.Height * 0.18);
        Show();
        Activate();
        RefreshState();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (LockedPanel.Visibility == Visibility.Visible)
            {
                UnlockBox.Focus();
            }
            else
            {
                Query.Focus();
            }
        });
    }

    private void HideWindow()
    {
        Query.Clear();
        UnlockBox.Clear();
        Results.ItemsSource = null;
        LockedMessage.Visibility = Visibility.Collapsed;
        Hide();
    }

    private void RefreshState()
    {
        var state = _services.Session.State;
        bool unlocked = state == VaultState.Unlocked;
        LockedPanel.Visibility = unlocked ? Visibility.Collapsed : Visibility.Visible;
        Query.IsEnabled = unlocked;
        if (!unlocked)
        {
            Results.ItemsSource = null;
            EmptyText.Visibility = Visibility.Collapsed;
            bool notCreated = state == VaultState.NotCreated;
            LockedTitle.Text = notCreated ? "Abre PassDesk para crear tu bóveda" : "PassDesk está bloqueado";
            LockedInputs.Visibility = notCreated ? Visibility.Collapsed : Visibility.Visible;
            HelloButton.Visibility = Visibility.Collapsed;
            if (!notCreated)
            {
                _ = UpdateHelloAsync();
            }

            return;
        }

        UpdateResults();
        if (IsVisible)
        {
            Query.Focus();
        }
    }

    private async Task UpdateHelloAsync()
    {
        bool show = _services.Settings.WindowsHelloQuickUnlock && _services.Session.CanQuickUnlock && await _services.Presence.IsAvailableAsync();
        HelloButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateResults();
    }

    private void UpdateResults()
    {
        if (_services.Session.State != VaultState.Unlocked)
        {
            return;
        }

        var results = CredentialSearch.Apply(_services.Session.Entries, Query.Text, null, SortMode.NameAscending)
            .Take(MaxResults)
            .Select(e => new EntryItemViewModel(e))
            .ToList();
        Results.ItemsSource = results;
        Results.SelectedIndex = results.Count > 0 ? 0 : -1;
        Results.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = results.Count == 0 && Query.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Escape:
                HideWindow();
                e.Handled = true;
                break;
            case Key.Down:
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter when modifiers == ModifierKeys.Control:
                OpenInMain();
                e.Handled = true;
                break;
            case Key.Enter when modifiers == ModifierKeys.Shift:
                CopySelected(ClipboardItemKind.Username);
                e.Handled = true;
                break;
            case Key.Enter:
                CopySelected(ClipboardItemKind.Password);
                e.Handled = true;
                break;
            case Key.O when modifiers == ModifierKeys.Control:
                OpenWebsite();
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (Results.Items.Count == 0)
        {
            return;
        }

        int index = Math.Clamp(Results.SelectedIndex + delta, 0, Results.Items.Count - 1);
        Results.SelectedIndex = index;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private EntryItemViewModel? Selected => Results.SelectedItem as EntryItemViewModel;

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => CopySelected(ClipboardItemKind.Password);

    private void CopySelected(ClipboardItemKind kind)
    {
        if (Selected?.Entry is not { } entry)
        {
            return;
        }

        string text = kind == ClipboardItemKind.Username ? entry.Username : entry.Password.Reveal();
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            _services.Clipboard.Copy(text, kind);
            HideWindow();
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Hint.Text = "No se pudo acceder al portapapeles. Inténtalo de nuevo.";
        }
    }

    private void OpenWebsite()
    {
        if (Selected?.Entry is { } entry && DomainUtil.TryCreateBrowsableUri(entry.Url, out var uri) && uri is not null)
        {
            HideWindow();
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
        }
    }

    private void OpenInMain()
    {
        if (Selected?.Entry is { } entry)
        {
            var id = entry.Id;
            HideWindow();
            _main.ShowEntry(id);
        }
    }

    private void OnUnlockKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnUnlock(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            HideWindow();
        }
    }

    private async void OnUnlock(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        using var password = SecureStringHelper.ToSensitiveChars(UnlockBox.SecurePassword);
        UnlockBox.Clear();
        if (password.Length == 0)
        {
            return;
        }

        _busy = true;
        UnlockResult result;
        try
        {
            result = await Task.Run(() => _services.Session.Unlock(password.Span));
        }
        finally
        {
            _busy = false;
        }

        ShowUnlockResult(result);
    }

    private async void OnHello(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UnlockResult result;
        try
        {
            result = await _services.Session.QuickUnlockAsync(_services.Presence, "Desbloquear PassDesk");
        }
        finally
        {
            _busy = false;
        }

        if (result.Status != UnlockStatus.Canceled)
        {
            ShowUnlockResult(result);
        }

        Activate();
    }

    private void ShowUnlockResult(UnlockResult result)
    {
        if (result.Succeeded)
        {
            LockedMessage.Visibility = Visibility.Collapsed;
            RefreshState();
            return;
        }

        LockedMessage.Text = result.Status switch
        {
            UnlockStatus.WrongPassword when result.RetryAfter > TimeSpan.Zero => $"Contraseña incorrecta. Espera {Math.Ceiling(result.RetryAfter.TotalSeconds):0} s.",
            UnlockStatus.WrongPassword => "Contraseña incorrecta.",
            UnlockStatus.Throttled => $"Demasiados intentos. Espera {Math.Ceiling(result.RetryAfter.TotalSeconds):0} s.",
            UnlockStatus.NotAvailable => "Windows Hello no está disponible. Usa la contraseña maestra.",
            UnlockStatus.CryptoUnavailable => PassDesk.Crypto.CryptoUnavailableException.UserMessage,
            _ => "No se pudo desbloquear. Abre PassDesk para más detalles.",
        };
        LockedMessage.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Danger");
        LockedMessage.Visibility = Visibility.Visible;
        UnlockBox.Focus();
    }
}
