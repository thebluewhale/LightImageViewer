using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer;

internal static partial class Program
{
    private static async Task Until(Func<bool> condition, int timeoutMs = 4000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds > timeoutMs) throw new Exception("Timed out waiting for state");
            await Task.Delay(15);
        }
    }

    private static async Task RunFeatureTests()
    {
        var folder = Path.Combine(_fixtures, "features");
        var decoder = new ImageDecoder();
        Task<DecodedImage> Decode(string name, DecodeOptions options = default) =>
            Task.Run(() => decoder.DecodeAsync(Path.Combine(folder, name), options, CancellationToken.None));

        await Test("pointer anchored zoom and clamped panning", () =>
        {
            var view = new ViewTransform();
            var image = new Size(1000, 800);
            var viewport = new Size(500, 400);
            view.Layout(image, viewport);
            Check(Math.Abs(view.Zoom - .5) < .001, "fit");
            view.ZoomAt(2, new Point(300, 240), image, viewport);
            Check(view.Zoom == 1 && view.Offset == new Vector(-50, -40), "pointer anchored");
            view.Pan(new Vector(10000, -10000), image, viewport);
            Check(view.Offset == new Vector(250, -200), "pan clamp");
            view.Fit(); view.Layout(image, viewport);
            Check(view.Offset == default && view.Zoom == .5, "fit resets pan");
            view.Actual(); view.Layout(image, viewport);
            Check(view.Zoom == 1 && view.Offset == default, "actual resets pan");
            view.ZoomAt(1000, new Point(250, 200), image, viewport);
            Check(view.Zoom == 32, "upper zoom bound");
            view.ZoomAt(.000001, new Point(250, 200), image, viewport);
            Check(view.Zoom == .01 && view.Offset == default, "lower zoom bound and center");
            return Task.CompletedTask;
        });
        await Test("fit follows resize, manual zoom persists", () =>
        {
            var view = new ViewTransform();
            view.Layout(new Size(1000, 800), new Size(500, 400));
            view.Layout(new Size(1000, 800), new Size(750, 600));
            Check(view.Zoom == .75, "fit resize");
            view.Actual();
            view.Layout(new Size(1000, 800), new Size(300, 200));
            Check(view.Zoom == 1, "manual resize");
            return Task.CompletedTask;
        });
        foreach (var name in new[] { "photo.jfif", "icon.ico" })
            await Test("additional format " + name, async () =>
            {
                Check(ImageCatalog.IsSupported(name), "catalog extension");
                Check((await Decode(name)).Width > 0, "decodes");
            });
        await Test("TIFF page dimensions and contents", async () =>
        {
            var first = await Decode("pages.tiff");
            var second = await Decode("pages.tiff", new DecodeOptions(PageIndex: 1));
            var third = await Decode("pages.tiff", new DecodeOptions(PageIndex: 2));
            Check(first.PageCount == 3 && first.Width == 120, "page count/first");
            Check(second.PageIndex == 1 && second.Width == 200 && second.Height == 140, "second page size");
            Check(Pixel(second, 20, 20)[0] == 255, "second page blue");
            Check(third.Width == 72 && third.Height == 96, "third page");
        });
        foreach (var extension in new[] { "gif", "webp" })
            await Test("animation timing, looping and compositing " + extension, async () =>
            {
                await Task.Run(async () =>
                {
                    using var animation = new AnimationSession(Path.Combine(folder, "motion." + extension));
                    Check(animation.FrameCount == 4, "frame count");
                    // GIF loop=1 means one repeat. WebP's loop field is a total iteration count.
                    Check(animation.RepetitionCount >= 0, "finite repetition");
                    var durations = new[] { 60, 90, 140, 110 };
                    for (var pass = 0; pass < 2; pass++)
                        for (var i = 0; i < animation.FrameCount; i++)
                        {
                            var frame = animation.ReadFrame(i, CancellationToken.None);
                            var expected = await decoder.DecodeAsync(Path.Combine(folder, $"expected-{extension}-{i}.png"), CancellationToken.None);
                            Check(frame.DurationMs == durations[i], "frame duration");
                            var actualBytes = new byte[96 * 64 * 4];
                            var expectedBytes = new byte[actualBytes.Length];
                            frame.Bitmap.CopyPixels(actualBytes, 96 * 4, 0);
                            expected.Bitmap.CopyPixels(expectedBytes, 96 * 4, 0);
                            // RGB values of fully transparent pixels are not visually meaningful.
                            for (var pixel = 0; pixel < actualBytes.Length; pixel += 4)
                                if (actualBytes[pixel + 3] == 0 && expectedBytes[pixel + 3] == 0)
                                    for (var channel = 0; channel < 3; channel++) actualBytes[pixel + channel] = expectedBytes[pixel + channel];
                            if (!actualBytes.SequenceEqual(expectedBytes))
                            {
                                var encoder = new PngBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(frame.Bitmap));
                                using var output = File.Create(Path.Combine(folder, $"actual-{extension}-{i}.png"));
                                encoder.Save(output);
                            }
                            Check(actualBytes.SequenceEqual(expectedBytes), $"composited frame {i}, pass {pass}");
                        }
                });
            });
        await Test("large JPEG preview preserves original dimensions with fewer pixels", async () =>
        {
            var timer = Stopwatch.StartNew();
            var preview = await Decode("large.jpg", new DecodeOptions(1536));
            var previewMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var full = await Decode("large.jpg");
            Console.WriteLine($"METRIC large JPEG: preview {previewMs:F1} ms; full {timer.Elapsed.TotalMilliseconds:F1} ms; preview pixels {preview.Bitmap.PixelWidth}x{preview.Bitmap.PixelHeight}; original 6000x4000");
            Check(preview.Width == 6000 && preview.Height == 4000 && preview.IsPreview, "metadata retained");
            Check(preview.Bitmap.PixelWidth <= 1536 && preview.Bitmap.PixelHeight <= 1536, "preview bounded");
            Check(!full.IsPreview && full.Bitmap.PixelWidth == 6000, "full detail");
        });
        await Test("cache hit, memory budget and modified-file invalidation", async () =>
        {
            var loader = new ImageLoader(decoder, 16 * 1024 * 1024);
            var path = Path.Combine(folder, "cache-test.png");
            File.Copy(Path.Combine(Gallery, "1.png"), path, true);
            var first = await loader.LoadAsync(path, default, CancellationToken.None);
            var timer = Stopwatch.StartNew();
            var second = await loader.LoadAsync(path, default, CancellationToken.None);
            Console.WriteLine($"METRIC cached load: {timer.Elapsed.TotalMilliseconds:F2} ms");
            Check(ReferenceEquals(first, second), "cache reused");
            File.Copy(Path.Combine(folder, "expected-webp-0.png"), path, true);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            var changed = await loader.LoadAsync(path, default, CancellationToken.None);
            Check(changed.Width == 96 && !ReferenceEquals(first, changed), "cache invalidated");
            Check(loader.CachedBytes <= 16 * 1024 * 1024, "bounded cache");
            loader.Clear();
            Check(loader.CachedBytes == 0, "cache cleared");
        });
        await Test("placement persistence and damaged-settings recovery", () =>
        {
            var path = Path.Combine(folder, "test-window.json");
            var placement = new WindowPlacement(100, 120, 900, 700, true);
            WindowPlacementStore.Save(path, placement);
            Check(WindowPlacementStore.Load(path) == placement, "roundtrip");
            File.WriteAllText(path, "{ broken json");
            Check(WindowPlacementStore.Load(path) is null, "invalid file ignored");
            Check(!new WindowPlacement(0, 0, double.NaN, 700, false).IsValid, "invalid dimensions");
            var recovered = NativeWindow.KeepVisible(new WindowPlacement(-99999, -99999, 900, 700, false));
            Check(recovered.Left > -99999 && recovered.Top > -99999, "disconnected monitor recovery");
            return Task.CompletedTask;
        });
        var window = new MainWindow(decoder);
        try
        {
            await Test("Home End direct navigation", async () =>
            {
                await window.OpenFileAsync(Path.Combine(Gallery, "4.webp"));
                await window.NavigateBoundaryAsync(true);
                Check(window.IndexLabel == "9/9", "End");
                await window.NavigateBoundaryAsync(false);
                Check(window.IndexLabel == "1/9", "Home");
            });
            await Test("toolbar actions and full-resolution refinement", async () =>
            {
                await window.OpenFileAsync(Path.Combine(folder, "large.jpg"));
                ((FrameworkElement)window.Content).Measure(new Size(1100, 700));
                ((FrameworkElement)window.Content).Arrange(new Rect(0, 0, 1100, 700));
                window.FitImage();
                Check(window.View.IsFit, "fit button action");
                window.ActualSize();
                await window.PendingRefinement;
                await Until(() => ((BitmapSource)((Image)window.FindName("Picture")).Source).PixelWidth == 6000);
                Check(window.View.Zoom == 1, "actual size");
                Check(((BitmapSource)((Image)window.FindName("Picture")).Source).PixelWidth == 6000, "refined source");
                window.ZoomBy(1.2);
                Check(Math.Abs(window.View.Zoom - 1.2) < .001, "zoom in");
                window.ZoomBy(1 / 1.2);
                Check(Math.Abs(window.View.Zoom - 1) < .001, "zoom out");
            });
            await Test("fullscreen hides chrome and restores normal geometry", () =>
            {
                window.Left = 100; window.Top = 120; window.Width = 900; window.Height = 700;
                window.ToggleFullscreen();
                Check(window.IsFullscreen && window.WindowStyle == WindowStyle.None, "borderless");
                Check(((FrameworkElement)window.FindName("Toolbar")).Visibility == Visibility.Collapsed, "toolbar hidden");
                window.ToggleFullscreen();
                Check(!window.IsFullscreen && window.WindowStyle == WindowStyle.SingleBorderWindow, "restored chrome");
                Check(window.Left == 100 && window.Top == 120 && window.Width == 900 && window.Height == 700, "restored bounds");
                return Task.CompletedTask;
            });
            await Test("TIFF window page navigation", async () =>
            {
                await window.OpenFileAsync(Path.Combine(folder, "pages.tiff"));
                await window.NavigatePageAsync(1);
                Check(window.ResolutionLabel == "200 × 140", "second page");
                await window.NavigatePageAsync(1);
                Check(window.ResolutionLabel == "72 × 96", "third page");
                await window.NavigatePageAsync(1);
                Check(window.ResolutionLabel == "72 × 96", "last page clamp");
                await window.NavigatePageAsync(-1);
                Check(window.ResolutionLabel == "200 × 140", "previous page");
            });
            await Test("animation playback pause resume and navigation cancellation", async () =>
            {
                await window.OpenFileAsync(Path.Combine(Gallery, "11.gif"));
                await Until(() => window.AnimationFrameIndex == 1);
                window.TogglePlayback();
                var paused = window.AnimationFrameIndex;
                await Task.Delay(250);
                Check(!window.IsPlaying && window.AnimationFrameIndex == paused, "paused");
                window.TogglePlayback();
                await Until(() => window.AnimationFrameIndex != paused);
                var oldPlayback = window.AnimationTask;
                await window.OpenFileAsync(Path.Combine(Gallery, "1.png"));
                await oldPlayback.WaitAsync(TimeSpan.FromSeconds(4));
                await Task.Delay(150);
                Check(window.DisplayedPath!.EndsWith("1.png") && !window.IsPlaying, "navigation stopped animation");
            });
            await Test("finite animation stops and can restart", async () =>
            {
                await window.OpenFileAsync(Path.Combine(folder, "motion.gif"));
                await window.AnimationTask.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!window.IsPlaying && window.AnimationFrameIndex == 3, "last frame retained");
                window.TogglePlayback();
                await Until(() => window.IsPlaying && window.AnimationFrameIndex == 0);
                await window.OpenFileAsync(Path.Combine(Gallery, "1.png"));
            });
            await Test("render toolbar and image without interactive desktop input", async () =>
            {
                await window.OpenFileAsync(Path.Combine(Gallery, "1.png"));
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(1100, 730));
                root.Arrange(new Rect(0, 0, 1100, 730));
                root.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1100, 730, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(_fixtures, "..", "viewer-v02.png"));
                encoder.Save(output);
            });
        }
        finally { window.Close(); }

        await Test("large folder enumeration benchmark", () =>
        {
            var folderPath = Path.Combine(_fixtures, "many-files");
            Directory.CreateDirectory(folderPath);
            for (var i = 1; i <= 5000; i++)
            {
                var path = Path.Combine(folderPath, i + ".png");
                if (!File.Exists(path)) File.WriteAllBytes(path, []);
            }
            var timer = Stopwatch.StartNew();
            var catalog = ImageCatalog.Open(Path.Combine(folderPath, "2500.png"));
            Console.WriteLine($"METRIC 5000-image directory: {timer.Elapsed.TotalMilliseconds:F1} ms");
            Check(catalog.Files.Length == 5000 && catalog.SelectedIndex == 2499, "natural order at scale");
            return Task.CompletedTask;
        });
    }
}
