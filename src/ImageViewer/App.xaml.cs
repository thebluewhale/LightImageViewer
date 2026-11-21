using System.IO;
using System.Windows;

namespace ImageViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLog.Write(args.Exception);
            MessageBox.Show("작업을 완료하지 못했습니다. 다른 파일을 열거나 프로그램을 다시 실행해 주세요.\n\n" +
                args.Exception.Message, "LightImageViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length > 0)
            _ = window.OpenFileAsync(e.Args[0]);
    }
}

internal static class ErrorLog
{
    public static void Write(Exception error)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightImageViewer");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "error.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                File.WriteAllText(path, "");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {error}\n");
        }
        catch { /* Logging must never prevent opening another image. */ }
    }
}
