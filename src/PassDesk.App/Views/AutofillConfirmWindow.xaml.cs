using System.Windows;
using System.Windows.Threading;
using PassDesk.App.Infrastructure;
using PassDesk.Auth;
using PassDesk.Browser.Broker;
using PassDesk.Browser.Origins;
using PassDesk.Browser.Protocol;

namespace PassDesk.App.Views;

/// <summary>
/// Confirmación del autorrelleno del navegador. Muestra qué cuenta se va a usar y en qué página, y no entrega nada hasta
/// que el usuario pulsa «Permitir». «Cancelar» es la opción por defecto y tiene el foco (Intro, Espacio o Esc despistados no
/// autorizan), «Permitir» no tiene tecla de acceso y solo se habilita tras un segundo seguido con la ventana activa, y la
/// ventana se cierra sola si la bóveda se bloquea. Nunca muestra la contraseña.
/// </summary>
public partial class AutofillConfirmWindow : Window
{
    private readonly AppServices _services;
    private readonly ConfirmArming _arming = new(TimeProvider.System, ConfirmArming.DefaultDelay);
    private readonly DispatcherTimer _armTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public AutofillConfirmWindow(AppServices services, FillConfirmation confirmation)
    {
        InitializeComponent();
        _services = services;
        WindowSecurity.Protect(this, services);
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        _armTimer.Tick += OnArmTick;
        Closed += (_, _) => _armTimer.Stop();

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
        // El botón ya está deshabilitado mientras no está armado; se vuelve a comprobar por si llega un clic en el mismo
        // instante en que la ventana pierde el foco.
        if (!_arming.IsArmed)
        {
            return;
        }

        // Si la bóveda se bloqueó mientras se mostraba la ventana, ya no se autoriza nada.
        Allowed = _services.Session.State == VaultState.Unlocked;
        Close();
    }

    // La ventana se abre con Show(), no con ShowDialog(): IsCancel/IsDefault solo hacen que Esc e Intro pulsen este botón,
    // no cierran la ventana por sí solos.
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnActivated(object? sender, EventArgs e)
    {
        _arming.Activated();
        AllowButton.IsEnabled = false;
        _armTimer.Start();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _arming.Deactivated();
        AllowButton.IsEnabled = false;
        _armTimer.Stop();
    }

    private void OnArmTick(object? sender, EventArgs e)
    {
        if (_arming.IsArmed)
        {
            AllowButton.IsEnabled = true;
            _armTimer.Stop();
        }
    }

    private void OnStateChanged(object? sender, VaultStateChangedEventArgs e)
    {
        if (e.NewState != VaultState.Unlocked)
        {
            Dispatcher.BeginInvoke(Close);
        }
    }
}
