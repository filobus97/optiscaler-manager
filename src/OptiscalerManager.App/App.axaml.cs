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
        }

        base.OnFrameworkInitializationCompleted();
    }
}
