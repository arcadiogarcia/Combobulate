using Microsoft.UI.Xaml;

namespace Combobulate.Sample.WinUI3;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        this.InitializeComponent();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

#if DEBUG
        try
        {
            var actionableApp = _window as zRover.Core.IActionableApp;
            await zRover.WinUI.RoverMcp.StartAsync(
                _window,
                "Combobulate.Sample.WinUI3",
                actionableApp: actionableApp,
                managerUrl: "http://localhost:5200");
            zRover.WinUI.RoverMcp.Log("App", "zRover MCP host started");
            _window.Closed += async (s, e) => await zRover.WinUI.RoverMcp.StopAsync();
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[zRover] startup failed: {ex}");
        }
#else
        await System.Threading.Tasks.Task.CompletedTask;
#endif
    }
}
