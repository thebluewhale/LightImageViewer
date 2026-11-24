using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer;

internal static partial class Program
{
    private static int _passed;
    private static int _failed;
    private static string _fixtures = "";
    private static string Gallery => Path.Combine(_fixtures, "갤러리 공백");

    [STAThread]
    private static int Main(string[] args)
    {
        _fixtures = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/fixtures");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await RunAll(); }
            catch (Exception error) { _failed++; Console.WriteLine("FAIL test harness: " + error); }
            Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
            app.Shutdown(_failed == 0 ? 0 : 1);
        };
        return app.Run();
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
    }

    private static async Task Test(string name, Func<Task> action)
    {
        try { await action(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { _failed++; Console.WriteLine("FAIL " + name + ": " + error); }
    }

    private static async Task Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static byte[] Pixel(DecodedImage image, int x, int y)
    {
        var pixel = new byte[4];
        image.Bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static async Task RunAll()
    {
        Check(Directory.Exists(Gallery), "Run python tests/create_fixtures.py first");
        var decoder = new ImageDecoder();
        Task<DecodedImage> Decode(string path) => Task.Run(() => decoder.DecodeAsync(path, CancellationToken.None));

        await Test("natural order, uppercase extension, Unicode path, nonrecursive scope", () =>
        {
            var catalog = ImageCatalog.Open(Path.Combine(Gallery, "10.BMP"));
            var names = catalog.Files.Select(Path.GetFileName).ToArray();
            Check(names.SequenceEqual(new[] { "1.png", "2.jpg", "3.jpeg", "4.webp", "10.BMP", "11.gif", "12.tiff", "13.webp", "20-corrupt.png" }), string.Join(",", names));
            Check(catalog.SelectedIndex == 4, "selected index");
            return Task.CompletedTask;
        });
        await Test("wrap first/last, single file, empty folder", () =>
        {
            Check(ImageCatalog.Wrap(0, -1, 9) == 8, "previous wraps");
            Check(ImageCatalog.Wrap(8, 1, 9) == 0, "next wraps");
            Check(ImageCatalog.Wrap(0, 1, 1) == 0, "single");
            Check(ImageCatalog.Wrap(-1, 1, 0) == -1, "empty");
            return Task.CompletedTask;
        });
        await Test("all six navigation key mappings", () =>
        {
            foreach (var key in new[] { Key.Right, Key.Down, Key.PageDown }) Check(MainWindow.NavigationDelta(key) == 1, key.ToString());
            foreach (var key in new[] { Key.Left, Key.Up, Key.PageUp }) Check(MainWindow.NavigationDelta(key) == -1, key.ToString());
            Check(MainWindow.NavigationDelta(Key.Space) == 0, "unbound key");
            return Task.CompletedTask;
        });
        foreach (var (file, width, height) in new[] {
            ("1.png", 960, 600), ("2.jpg", 1200, 800), ("3.jpeg", 80, 120),
            ("4.webp", 960, 600), ("10.BMP", 120, 80), ("11.gif", 64, 48), ("12.tiff", 120, 80), ("13.webp", 64, 48) })
        {
            await Test("decode " + file, async () =>
            {
                var path = Path.Combine(Gallery, file);
                var result = await Decode(path);
                Check(result.Width == width && result.Height == height, "dimensions");
                Check(result.FileBytes == new FileInfo(path).Length, "file size");
                Check(result.Bitmap.IsFrozen, "cross-thread bitmap");
                using var exclusive = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            });
        }
        foreach (var file in new[] { "1.png", "4.webp" })
            await Test("alpha preserved " + file, async () =>
            {
                var image = await Decode(Path.Combine(Gallery, file));
                Check(Pixel(image, 0, 0)[3] == 0, "transparent pixel");
                Check(Math.Abs(Pixel(image, 500, 440)[3] - 120) <= 1, "partial alpha");
            });
        foreach (var file in new[] { "11.gif", "13.webp" })
            await Test("animation first frame " + file, async () =>
            {
                var pixel = Pixel(await Decode(Path.Combine(Gallery, file)), 10, 10);
                Check(pixel[2] > 240 && pixel[0] < 15, "red first frame");
            });

        // Expected top-left colors for every EXIF orientation, independent of transform code.
        var corners = new[] { 0, 1, 3, 2, 0, 2, 3, 1 };
        var colors = new[] { new[] { 30, 30, 240 }, new[] { 30, 220, 30 }, new[] { 240, 30, 30 }, new[] { 30, 220, 240 } };
        for (var orientation = 1; orientation <= 8; orientation++)
        {
            var value = orientation;
            await Test("EXIF orientation " + value, async () =>
            {
                var result = await Decode(Path.Combine(_fixtures, "orientation", value + ".jpg"));
                Check(result.Width == (value >= 5 ? 80 : 120) && result.Height == (value >= 5 ? 120 : 80), "oriented dimensions");
                var pixel = Pixel(result, 8, 8);
                for (var channel = 0; channel < 3; channel++)
                    Check(Math.Abs(pixel[channel] - colors[corners[value - 1]][channel]) < 15, $"corner {channel}: {pixel[channel]}");
            });
        }
        await Test("corrupt file rejected", () => Expect<InvalidDataException>(() => Decode(Path.Combine(Gallery, "20-corrupt.png"))));
        await Test("missing file rejected", () => Expect<FileNotFoundException>(() => Decode(Path.Combine(Gallery, "deleted.png"))));
        await Test("oversized dimensions rejected before pixel allocation", () => Expect<NotSupportedException>(() => Decode(Path.Combine(_fixtures, "oversized.png"))));
        await Test("unsupported extension", () => Expect<NotSupportedException>(() => Task.Run(() => ImageCatalog.Open(Path.Combine(Gallery, "메모.txt")))));
        await Test("canceled decode", () => Expect<OperationCanceledException>(() => Task.Run(() => decoder.DecodeAsync(Path.Combine(Gallery, "1.png"), new CancellationToken(true)))));

        var window = new MainWindow(new ImageDecoder());
        try
        {
            await Test("window blank state", async () =>
            {
                Check(window.IndexLabel == "0/0", "empty index");
                await window.NavigateAsync(1);
                Check(window.DisplayedPath is null, "empty navigation");
            });
            await Test("window open and metadata", async () =>
            {
                await window.OpenFileAsync(Path.Combine(Gallery, "3.jpeg"));
                Check(window.IndexLabel == "3/9", "index");
                Check(window.ResolutionLabel == "80 × 120", "resolution");
                Check(window.Title.StartsWith("3.jpeg"), "title");
                Check(window.DisplayedPath == window.SelectedPath, "displayed selection");
            });
            await Test("window corrupted file and navigation recovery", async () =>
            {
                await window.OpenFileAsync(Path.Combine(Gallery, "20-corrupt.png"));
                Check(window.Status == "읽기 실패" && window.IndexLabel == "9/9", "failure stays navigable");
                Check(window.DisplayedPath is null && window.ResolutionLabel == "— × —", "no stale pixels/metadata");
                await window.NavigateAsync(1);
                Check(window.IndexLabel == "1/9" && window.DisplayedPath!.EndsWith("1.png"), "wrap recovers");
                await window.NavigateAsync(-1);
                Check(window.IndexLabel == "9/9", "backward wrap");
            });
            await Test("window rapid 100 key-equivalent navigations", async () =>
            {
                await window.OpenFileAsync(Path.Combine(Gallery, "1.png"));
                var tasks = Enumerable.Range(0, 100).Select(_ => window.NavigateAsync(1)).ToArray();
                await Task.WhenAll(tasks);
                Check(window.IndexLabel == "2/9", "all keys counted");
                Check(window.DisplayedPath!.EndsWith("2.jpg"), "only final image displayed");
            });
            await Test("window concurrent file opens: latest wins", async () =>
            {
                var first = window.OpenFileAsync(Path.Combine(Gallery, "2.jpg"));
                var second = window.OpenFileAsync(Path.Combine(Gallery, "4.webp"));
                await Task.WhenAll(first, second);
                Check(window.DisplayedPath!.EndsWith("4.webp") && window.IndexLabel == "4/9", "final open wins");
            });
            await Test("window drop event opens Unicode path", async () =>
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { Path.Combine(Gallery, "메모.txt"), Path.Combine(Gallery, "10.BMP") });
                var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                    null, new object[] { data, DragDropKeyStates.None, DragDropEffects.Copy, window, new Point(0, 0) }, null)!;
                drop.RoutedEvent = UIElement.DropEvent;
                window.RaiseEvent(drop);
                await window.PendingOperation;
                Check(window.DisplayedPath!.EndsWith("10.BMP") && drop.Handled, "drop result");
            });
            await Test("deleted file during gallery navigation and recovery", async () =>
            {
                var folder = Path.Combine(_fixtures, "deletion");
                Directory.CreateDirectory(folder);
                File.Copy(Path.Combine(Gallery, "1.png"), Path.Combine(folder, "1.png"), true);
                File.Copy(Path.Combine(Gallery, "1.png"), Path.Combine(folder, "2.png"), true);
                await window.OpenFileAsync(Path.Combine(folder, "1.png"));
                File.Delete(Path.Combine(folder, "2.png"));
                await window.NavigateAsync(1);
                Check(window.Status == "읽기 실패" && window.IndexLabel == "2/2", "deleted error");
                await window.NavigateAsync(1);
                Check(window.DisplayedPath!.EndsWith("1.png"), "recovered");
            });
        }
        finally { window.Close(); }

        var slowDecoder = new DelayedDecoder();
        var raceWindow = new MainWindow(slowDecoder);
        await Test("slow uncooperative decoder: bounded concurrency and stale suppression", async () =>
        {
            var first = raceWindow.OpenFileAsync(Path.Combine(Gallery, "1.png"));
            await slowDecoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var latest = raceWindow.NavigateAsync(1);
            await Task.WhenAll(first, latest);
            Check(slowDecoder.MaxConcurrent == 1, "single decoder at a time");
            Check(raceWindow.DisplayedPath!.EndsWith("2.jpg"), "stale result ignored");
        });
        await Test("close with pending decoder", async () =>
        {
            var pending = raceWindow.NavigateAsync(1);
            raceWindow.Close();
            await pending;
            Check(raceWindow.DisplayedPath is null || !raceWindow.DisplayedPath.EndsWith("3.jpeg"), "no post-close update");
        });
        await RunFeatureTests();
        await RunLoadingFeedbackTests();
        await RunTransitionTests();
        await RunViewModeTests();
        await RunExtendedFormatTests();
    }

    private sealed class DelayedDecoder : IImageDecoder
    {
        private int _concurrent;
        public int MaxConcurrent { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DecodedImage> DecodeAsync(string path, CancellationToken cancellationToken)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _concurrent));
            Started.TrySetResult();
            try
            {
                await Task.Delay(200); // Intentionally ignore cancellation to exercise stale protection.
                var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
                bitmap.Freeze();
                return new DecodedImage(bitmap, 1, 1, 4);
            }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
    }
}
