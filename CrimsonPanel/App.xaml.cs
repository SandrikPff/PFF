using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace CrimsonPanel;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pff_crimson", "startup.log");
    private Mutex? _instance;

    public static void LogError(string stage, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {stage}\n{ex}\n\n");
        }
        catch { /* Never crash while trying to report an error. */ }
    }

    private async void App_Startup(object sender, StartupEventArgs e)
    {
        if (e.Args.Contains("--detach-apo", StringComparer.Ordinal))
        {
            try
            {
                if (ApoBridge.Find() is { } installation)
                {
                    string? captureGuid = ApoBridge.ReadCaptureRoute(installation);
                    try { ApoBridge.Disconnect(installation); }
                    finally
                    {
                        if (installation.SupportsAutomaticRouting && captureGuid is not null)
                            await ApoBridge.RemoveCaptureAsync(installation, captureGuid);
                    }
                }
                Shutdown();
            }
            catch (Exception ex)
            {
                LogError("Отключение профиля APO при удалении", ex);
                Shutdown(1);
            }
            return;
        }
        _instance = new Mutex(initiallyOwned: true, @"Local\Pff_crimson", out bool firstInstance);
        if (!firstInstance)
        {
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) LogError("Необработанная ошибка", ex);
        };
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            LogError("Ошибка открытия окна", ex);
            MessageBox.Show($"PFF не смог запуститься.\n\n{ex}\n\nЖурнал: {LogPath}",
                "PFF — ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError("Ошибка интерфейса", e.Exception);
        MessageBox.Show($"Ошибка: {e.Exception.Message}\n\nЖурнал: {LogPath}",
            "PFF", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
