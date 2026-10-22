using System.Windows;
using PassDesk.App.Infrastructure;
using PassDesk.Auth;
using PassDesk.Browser.Broker;
using PassDesk.Browser.Origins;
using PassDesk.Browser.Protocol;

namespace PassDesk.App.Views;

/// <summary>
/// Confirmación del autorrelleno del navegador. Muestra qué cuenta se va a usar y en qué página, y no entrega nada hasta
/// que el usuario pulsa «Permitir». «Cancelar» es la opción por defecto (una pulsación de Intro despistada no autoriza) y la
/// ventana se cierra sola si la bóveda se bloquea. Nunca muestra la contraseña.
/// </summary>
public partial class AutofillConfirmWindow : Window
{
    private readonly AppServices _services;

    public AutofillConfirmWindow(AppServices services, FillConfirmation confirmation)
    {
        InitializeComponent();
        _services = services;
        WindowSecurity.Protect(this, services);

        string what = confirmation.Scope switch
        {
            FillScope.Username => "el usuario",
            FillScope.Password => "la contraseña",
            _ => "el usuario y la contraseña",
        };
        Summary.Text = $"La extensión de PassDesk pide rellenar {what} de esta cuenta:";
        AccountText.Text = confirmation.Title.Length > 0 ? confirmation.Title : confirmation.StoredSite;
        UserText.Text = confirmation.Username.Length > 0 ? confirmation.Username : "(sin usuario)";
        OriginText.Text = confirmation.PageOrigin.Serialize();
        if (confirmation.Kind == OriginMatchKind.Related)
        {
            Warning.Text = $"Atención: esta cuenta se guardó para {confirmation.StoredSite}, no para esta página exacta (es otro subdominio del mismo sitio).";
            Warning.Visibility = Visibility.Visible;
        }

        services.Session.StateChanged += OnStateChanged;
        Closed += (_, _) => services.Session.StateChanged -= OnStateChanged;
    }

    /// <summary>Verdadero solo si el usuario pulsó «Permitir».</summary>
    public bool Allowed { get; private set; }

    private void OnAllow(object sender, RoutedEventArgs e)
    {
        // Si la bóveda se bloqueó mientras se mostraba la ventana, ya no se autoriza nada.
        Allowed = _services.Session.State == VaultState.Unlocked;
        Close();
    }

    private void OnStateChanged(object? sender, VaultStateChangedEventArgs e)
    {
        if (e.NewState != VaultState.Unlocked)
        {
            Dispatcher.BeginInvoke(Close);
        }
    }
}
