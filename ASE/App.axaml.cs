using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace ASE
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // Workaround for an upstream race. Avalonia keeps the assembly behind each avares://
            // authority in a Dictionary with no lock, filled on first use, and
            // Svg.Controls.Skia.Avalonia (12.0.0.13+) loads every <Svg Path=..> on the thread pool:
            // the first burst of loads while MainWindow's XAML is being built can corrupt that
            // cache, which Avalonia then reports as "The resource ... could not be found", silent
            // for the menu icons, fatal for the drive LED bitmaps, which load on this thread.
            // Resolving ASE's entry here, on one thread and before any window exists, leaves every
            // later access a read. The literal must be spelled as the XAML spells it: the cache is
            // keyed by the authority, case-sensitively. Public API only, so once the race is fixed
            // upstream this is a redundant lookup and the line can go.
            AssetLoader.GetAssembly(new Uri("avares://ASE/"));

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new MainWindow();

            base.OnFrameworkInitializationCompleted();
        }
    }
}
