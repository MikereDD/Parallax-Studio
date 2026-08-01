using System.IO;
using System.Windows;
using System.Windows.Threading;
using ParallaxStudio.App.Services;
using ParallaxStudio.App.ViewModels;
using ParallaxStudio.Core;
using ParallaxStudio.Core.Services;
using ParallaxStudio.Infrastructure;
using ParallaxStudio.Infrastructure.Discs;
using ParallaxStudio.Infrastructure.Logging;
using ParallaxStudio.Infrastructure.Persistence;
using ParallaxStudio.Infrastructure.Playback;

namespace ParallaxStudio.App;

public partial class App : Application
{
    private FileLogger? _logger;
    private MainViewModel? _mainViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths? paths = null;
        try
        {
            paths = new AppPaths();
            paths.EnsureCreated();
            _logger = new FileLogger(paths);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            ISettingsService settings = new JsonSettingsService(paths, _logger);
            IResumeStore resume = new JsonResumeStore(paths, _logger);
            IDiscService discs = new OpticalDiscService(_logger);
            IPlaybackService playback = new MpvPlaybackService(_logger);
            var filePicker = new FilePickerService();

            _mainViewModel = new MainViewModel(playback, settings, resume, discs, _logger, filePicker);
            var window = new MainWindow(_mainViewModel);
            MainWindow = window;

            _logger.InfoAsync($"Starting {AppIdentity.Name} v{AppIdentity.Version}.").GetAwaiter().GetResult();
            window.Show();
        }
        catch (Exception ex)
        {
            try
            {
                _logger?.ErrorAsync("Fatal startup failure.", ex).GetAwaiter().GetResult();
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Parallax Studio", "logs", "startup-failure.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
                File.WriteAllText(fallback, ex.ToString());
            }
            catch { }

            MessageBox.Show(ex.ToString(), $"{AppIdentity.Name} — Startup failure", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _mainViewModel?.ShutdownAsync().GetAwaiter().GetResult();
            _logger?.InfoAsync($"Stopped {AppIdentity.Name} v{AppIdentity.Version}.").GetAwaiter().GetResult();
        }
        catch
        {
            // The process is already exiting; startup logging retains prior failures.
        }
        finally
        {
            _logger?.Dispose();
        }

        base.OnExit(e);
    }

    private async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_logger is not null) await _logger.ErrorAsync("Unhandled UI exception.", e.Exception);
        MessageBox.Show(e.Exception.Message, AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (_logger is not null && e.ExceptionObject is Exception exception)
        {
            _logger.ErrorAsync("Unhandled application-domain exception.", exception).GetAwaiter().GetResult();
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _ = _logger?.ErrorAsync("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
