using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PassDesk.App.Infrastructure;
using PassDesk.App.Views;
using PassDesk.Auth;
using PassDesk.Core.Diagnostics;
using PassDesk.Storage;

namespace PassDesk.App;

public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool startInTray = StartupRegistration.IsTrayLaunch(e.Args);

        var paths = AppPaths.CreateDefault();
        _singleInstance = SingleInstance.TryAcquire(paths.Root, out string activationEvent);
        if (_singleInstance is null)
        {
            // Ya hay una instancia abierta: se activa (salvo al iniciar Windows, que debe quedarse en la bandeja) y esta termina.
            if (!startInTray)
            {
                SingleInstance.SignalExisting(activationEvent);
            }

            Shutdown();
            return;
        }

        paths.EnsureCreated();
        AppLog.AddSink(new FileLogSink(paths.LogDirectory));
        RegisterGlobalExceptionHandlers();

        _services = AppServices.Create(paths, Dispatcher);
        _services.Theme.Apply(_services.Settings.Theme);

        var main = new MainWindow(_services);
        MainWindow = main;
        _singleInstance.ActivationRequested += (_, _) => Dispatcher.BeginInvoke(main.BringToFront);

        // El navegador pide desbloquear: solo se muestra PassDesk; el usuario se autentica con los mecanismos de siempre.
        _services.Autofill.UnlockRequested += (_, _) => main.BringToFront();
        main.ShowTrayIcon();
        if (startInTray)
        {
            // «Iniciar con Windows»: sin ventana y con la bóveda bloqueada (siempre lo está al arrancar). El atajo global
            // necesita igualmente la ventana, aunque esté oculta.
            new WindowInteropHelper(main).EnsureHandle();
        }
        else
        {
            main.Show();
        }

        AppLog.Info(LogEvent.AppStarted, new LogMetric("tray", startInTray ? 1 : 0));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services is not null)
        {
            _services.LockNow(LockReason.Exit);
            _services.Dispose();
            AppLog.Info(LogEvent.AppExiting);
        }

        _singleInstance?.Dispose();
        base.OnExit(e);

        // Bóveda bloqueada, portapapeles limpio y todo guardado: si se usó el visor PDF se termina ya,
        // sin esperar al motor PDF de Windows, que puede bloquear el cierre (ver NativeMethods).
        NativeMethods.FinishProcessIfPdfEngineLoaded(e.ApplicationExitCode);
    }

    /// <summary>
    /// Ante un error inesperado se registra solo el tipo de excepción (nunca su mensaje), se bloquea
    /// la bóveda y se muestra un mensaje genérico sin detalles internos.
    /// </summary>
    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error(LogEvent.UnhandledException, args.Exception);
            args.Handled = true;
            try
            {
                _services?.LockNow(LockReason.Manual);
            }
            catch (Exception lockError)
            {
                AppLog.Error(LogEvent.UnhandledException, lockError);
            }

            MessageBox.Show(
                "Se produjo un error inesperado. Por seguridad, PassDesk se ha bloqueado.\n\n" +
                "Tus datos siguen cifrados. Si el problema se repite, reinicia la aplicación.",
                "PassDesk",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppLog.Error(LogEvent.UnhandledException, ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error(LogEvent.UnhandledException, args.Exception);
            args.SetObserved();
        };
    }
}
