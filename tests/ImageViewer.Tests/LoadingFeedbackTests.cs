using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer;

internal static partial class Program
{
    private static async Task RunLoadingFeedbackTests()
    {
        var path = Path.Combine(Gallery, "1.png");
        static bool MessageVisible(MainWindow window) => ((StackPanel)window.FindName("MessagePanel")).Visibility == Visibility.Visible;

        await Test("fast load has no immediate or delayed loading message", async () =>
        {
            var decoder = new GatedFeedbackDecoder();
            decoder.Release.TrySetResult();
            var window = new MainWindow(decoder);
            try
            {
                var opening = window.OpenFileAsync(path);
                Check(window.Status == "" && !MessageVisible(window), "no immediate loading text");
                await opening;
                await Task.Delay(MainWindow.LoadingFeedbackDelayMs + 100);
                Check(window.DisplayedPath == path && window.Status == "" && !MessageVisible(window), "completed image is not covered by a late timer");
            }
            finally { window.Close(); }
        });

        await Test("slow load shows feedback only after delay and clears it on completion", async () =>
        {
            var decoder = new GatedFeedbackDecoder();
            var window = new MainWindow(decoder);
            try
            {
                var opening = window.OpenFileAsync(path);
                Check(window.Status == "" && !MessageVisible(window), "starts without loading text");
                await decoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => window.Status == "불러오는 중…");
                Check(MessageVisible(window) && window.DisplayedPath is null, "feedback while decoding is pending");
                decoder.Release.TrySetResult();
                await opening;
                Check(window.DisplayedPath == path && window.Status == "" && !MessageVisible(window), "loading feedback removed");
            }
            finally { decoder.Release.TrySetResult(); window.Close(); }
        });

        await Test("canceled load timer cannot overwrite a newer error", async () =>
        {
            var decoder = new GatedFeedbackDecoder();
            var window = new MainWindow(decoder);
            try
            {
                var opening = window.OpenFileAsync(path);
                await decoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await window.OpenFileAsync(Path.Combine(Gallery, "unsupported.txt"));
                decoder.Release.TrySetResult();
                await opening;
                await Task.Delay(MainWindow.LoadingFeedbackDelayMs + 100);
                Check(window.Status == "읽기 실패" && MessageVisible(window), "new error remains visible");
                Check(((TextBlock)window.FindName("MessageTitle")).Text == "이미지를 열 수 없습니다", "stale loading title suppressed");
            }
            finally { decoder.Release.TrySetResult(); window.Close(); }
        });

        await Test("closing while loading cancels delayed feedback", async () =>
        {
            var decoder = new GatedFeedbackDecoder();
            var window = new MainWindow(decoder);
            try
            {
                var opening = window.OpenFileAsync(path);
                await decoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                window.Close();
                decoder.Release.TrySetResult();
                await opening;
                await Task.Delay(MainWindow.LoadingFeedbackDelayMs + 100);
                Check(window.Status == "" && !MessageVisible(window), "closed window is not updated");
            }
            finally { decoder.Release.TrySetResult(); window.Close(); }
        });
    }

    private sealed class GatedFeedbackDecoder : IImageDecoder
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DecodedImage> DecodeAsync(string path, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            // Ignore cancellation to verify the window's own stale-feedback protection.
            await Release.Task;
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            bitmap.Freeze();
            return new DecodedImage(bitmap, 1, 1, 4);
        }
    }
}
