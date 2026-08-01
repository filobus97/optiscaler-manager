// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OptiscalerManager.App.Infrastructure;
using OptiscalerManager.App.Services;
using OptiscalerManager.App.Views;
using OptiscalerManager.Core.Logging;

namespace OptiscalerManager.App;

public partial class App : Application
{
    /// <summary>
    /// Controller-to-UI bridge, shared so Settings can report what is connected.
    /// Null until the desktop lifetime starts.
    /// </summary>
    public static GamepadNavigator? Gamepad { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Install the logging sink so the ported service layer's diagnostics
        // (previously routed to a DebugWindow) are captured.
        Log.SetSink(new UiLog());

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Window? MainWindowAccessor() => desktop.MainWindow;

            // The manual-component provider needs the active window for its file
            // picker, so it resolves it lazily through this accessor.
            var manager = new ManagerService(new AvaloniaManualComponentProvider(MainWindowAccessor));
            desktop.MainWindow = new MainWindow(manager);

            // Drive the UI from a game controller (Linux/evdev). Best-effort: a
            // failure here must never stop the app from starting.
            try
            {
                Gamepad = new GamepadNavigator();
                if (manager.GamepadNavigationEnabled) Gamepad.Start();
            }
            catch (System.Exception ex)
            {
                Log.Write($"[Gamepad] Disabled: {ex.Message}");
            }

            desktop.ShutdownRequested += (_, _) => Gamepad?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
