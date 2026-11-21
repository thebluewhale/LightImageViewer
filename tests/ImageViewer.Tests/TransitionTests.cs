using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ImageViewer;

internal static partial class Program
{
    private static async Task RunTransitionTests()
    {
        var firstPath = Path.Combine(Gallery, "1.png");
        var blockedPath = Path.Combine(Gallery, "2.jpg");
        var finalPath = Path.Combine(Gallery, "3.jpeg");

        await Test("transition retains image, title and metadata until replacement is ready", async () =>
        {
            var decoder = new TransitionDecoder(blockedPath);
            var window = new MainWindow(decoder);
            try
            {
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(1100, 700)); root.Arrange(new Rect(0, 0, 1100, 700));
                await window.OpenFileAsync(firstPath);
                window.ZoomBy(1.2);
                var picture = (Image)window.FindName("Picture");
                var oldSource = picture.Source;
                var oldTitle = window.Title;
                var oldResolution = window.ResolutionLabel;
                var oldZoom = window.View.Zoom;
                var next = window.NavigateAsync(1);
                Check(ReferenceEquals(picture.Source, oldSource) && window.View.Zoom == oldZoom, "no blank frame or transform reset at navigation start");
                await decoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => window.Status == "불러오는 중…");
                Check(window.DisplayedPath == firstPath && window.Title == oldTitle && window.ResolutionLabel == oldResolution, "displayed metadata remains consistent");
                Check(((Border)window.FindName("ImageFrame")).Visibility == Visibility.Visible && ReferenceEquals(picture.Source, oldSource), "previous image remains visible while blocked");
                Check(((StackPanel)window.FindName("MessagePanel")).Visibility == Visibility.Collapsed, "slow-load text does not cover the previous image");
                decoder.Release.TrySetResult();
                await next;
                Check(window.DisplayedPath == blockedPath && !ReferenceEquals(picture.Source, oldSource), "new bitmap replaces old bitmap");
                Check(window.Title.StartsWith("2.jpg") && window.ResolutionLabel == "1200 × 800" && window.View.IsFit, "new metadata and fit transform committed");
                Check(window.Status == "", "loading indication removed");
            }
            finally { decoder.Release.TrySetResult(); window.Close(); }
        });

        await Test("rapid navigation keeps the last displayed image and discards intermediate decode", async () =>
        {
            var decoder = new TransitionDecoder(blockedPath);
            var window = new MainWindow(decoder);
            try
            {
                await window.OpenFileAsync(firstPath);
                var picture = (Image)window.FindName("Picture");
                var oldSource = picture.Source;
                var intermediate = window.NavigateAsync(1);
                await decoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var latest = window.NavigateAsync(1);
                Check(window.SelectedPath == finalPath && window.DisplayedPath == firstPath && ReferenceEquals(oldSource, picture.Source), "last displayed image is retained across canceled selection");
                decoder.Release.TrySetResult();
                await Task.WhenAll(intermediate, latest);
                Check(window.DisplayedPath == finalPath && window.Title.StartsWith("3.jpeg"), "latest selection wins");
                await Task.Delay(MainWindow.LoadingFeedbackDelayMs + 100);
                Check(window.Status == "" && ((StackPanel)window.FindName("MessagePanel")).Visibility == Visibility.Collapsed, "no stale loading overlay");
            }
            finally { decoder.Release.TrySetResult(); window.Close(); }
        });

        await Test("zooming retained preview cannot start refinement for pending selection", async () =>
        {
            var folder = Path.Combine(_fixtures, "features");
            var previewPath = Path.Combine(folder, "large.jpg");
            var decoder = new TransitionDecoder(blockedPath);
            var window = new MainWindow(decoder);
            try
            {
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(1100, 700)); root.Arrange(new Rect(0, 0, 1100, 700));
                await window.OpenFileAsync(previewPath);
                var opening = window.OpenFileAsync(blockedPath);
                await decoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var source = ((Image)window.FindName("Picture")).Source;
                var previousRefinement = window.PendingRefinement;
                window.ActualSize(); window.ZoomBy(1.2);
                await Task.Delay(220);
                Check(ReferenceEquals(previousRefinement, window.PendingRefinement), "no refinement scheduled from the previous image metadata");
                Check(ReferenceEquals(source, ((Image)window.FindName("Picture")).Source) && window.DisplayedPath == previewPath, "retained preview remains displayed during zoom");
                decoder.Release.TrySetResult(); await opening;
                Check(window.DisplayedPath == blockedPath && window.ResolutionLabel == "1200 × 800", "pending image commits correctly");
            }
            finally { decoder.Release.TrySetResult(); window.Close(); }
        });

        await Test("retained animation cannot restart against a pending image", async () =>
        {
            var decoder = new TransitionDecoder(blockedPath);
            var window = new MainWindow(decoder);
            try
            {
                await window.OpenFileAsync(Path.Combine(Gallery, "11.gif"));
                await Until(() => window.AnimationFrameIndex == 1);
                var oldPlayback = window.AnimationTask;
                var oldSource = ((Image)window.FindName("Picture")).Source;
                var opening = window.OpenFileAsync(blockedPath);
                await decoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                window.TogglePlayback();
                await oldPlayback.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!window.IsPlaying && !((Button)window.FindName("PlayButton")).IsEnabled, "playback disabled during transition");
                Check(ReferenceEquals(oldSource, ((Image)window.FindName("Picture")).Source), "last animation frame is retained");
                decoder.Release.TrySetResult(); await opening;
                Check(window.DisplayedPath == blockedPath && !window.IsPlaying, "static replacement remains static");
            }
            finally { decoder.Release.TrySetResult(); window.Close(); }
        });

        await Test("failed replacement shows an error and remains navigable", async () =>
        {
            var decoder = new TransitionDecoder(blockedPath, fail: true);
            var window = new MainWindow(decoder);
            try
            {
                await window.OpenFileAsync(firstPath);
                var opening = window.NavigateAsync(1);
                await decoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(window.DisplayedPath == firstPath, "retain previous image until failure is known");
                decoder.Release.TrySetResult(); await opening;
                Check(window.DisplayedPath is null && window.Status == "읽기 실패" && window.Title.StartsWith("2.jpg"), "failure identifies selected file without mislabeling old pixels");
                Check(((Image)window.FindName("Picture")).Source is null, "failed image is not mistaken for previous image");
                await window.NavigateAsync(-1);
                Check(window.DisplayedPath == firstPath && window.Status == "", "navigation recovers");
            }
            finally { decoder.Release.TrySetResult(); window.Close(); }
        });
    }

    private sealed class TransitionDecoder(string blockedPath, bool fail = false) : IImageDecoder
    {
        private readonly ImageDecoder _decoder = new();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DecodedImage> DecodeAsync(string path, CancellationToken token) => DecodeAsync(path, new DecodeOptions(), token);
        public async Task<DecodedImage> DecodeAsync(string path, DecodeOptions options, CancellationToken token)
        {
            if (string.Equals(path, blockedPath, StringComparison.OrdinalIgnoreCase))
            {
                Started.TrySetResult();
                await Release.Task; // An uncooperative decoder must not replace a newer selection.
                if (fail) throw new InvalidDataException("Intentional decode failure");
                return await _decoder.DecodeAsync(path, options, CancellationToken.None);
            }
            return await _decoder.DecodeAsync(path, options, token);
        }
    }
}
