using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace Combobulate.Sample.Uwp;

sealed partial class App : Application
{
    public App()
    {
        this.InitializeComponent();
        this.Suspending += OnSuspending;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        var rootFrame = Window.Current.Content as Frame;

        if (rootFrame == null)
        {
            rootFrame = new Frame();
            rootFrame.NavigationFailed += (sender, args) =>
                throw new Exception("Failed to load Page " + args.SourcePageType.FullName);

            Window.Current.Content = rootFrame;
        }

        if (e.PrelaunchActivated == false)
        {
            if (rootFrame.Content == null)
            {
                rootFrame.Navigate(typeof(MainPage), e.Arguments);
            }

            Window.Current.Activate();
        }
    }

    private void OnSuspending(object sender, SuspendingEventArgs e)
    {
        var deferral = e.SuspendingOperation.GetDeferral();
        deferral.Complete();
    }
}
