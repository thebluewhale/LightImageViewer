using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer;

internal static partial class Program
{
    private static async Task RunViewModeTests()
    {
        var folder = Path.Combine(_fixtures, "view-mode");
        Directory.CreateDirectory(folder);
        foreach (var (name, width, height) in new[] { ("1.png",800,600), ("2.png",400,300) })
        {
            var pixels = new byte[width * height * 4];
            for (var index = 0; index < pixels.Length; index += 4)
            { pixels[index] = 180; pixels[index + 1] = 80; pixels[index + 2] = 40; pixels[index + 3] = 255; }
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.Create(Path.Combine(folder, name)); encoder.Save(stream);
        }
        var first = Path.Combine(folder, "1.png");
        var second = Path.Combine(folder, "2.png");
        static void Layout(MainWindow window)
        {
            window.Left = 100; window.Top = 120; window.Width = 1600; window.Height = 1300;
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(1600,1300)); root.Arrange(new Rect(0,0,1600,1300));
        }
        static void CheckFit(MainWindow window)
        {
            var viewport = (FrameworkElement)window.FindName("Viewport");
            var frame = (FrameworkElement)window.FindName("ImageFrame");
            Check(window.View.IsFit && window.View.Zoom > 1, "small image is enlarged");
            Check(frame.Width <= viewport.ActualWidth + .01 && frame.Height <= viewport.ActualHeight + .01, "aspect ratio fit stays inside viewport");
            Check(Math.Abs(frame.Width-viewport.ActualWidth)<.01 || Math.Abs(frame.Height-viewport.ActualHeight)<.01, "one dimension fills available space");
        }
        await Test("fit enlarges an 800x600 image and preserves aspect ratio", () =>
        {
            var view = new ViewTransform();
            view.Fit(); view.Layout(new Size(800,600),new Size(1600,1200));
            Check(view.Zoom==2 && view.Offset==default,"full-size fit above 100 percent");
            view.Layout(new Size(800,600),new Size(1600,900));
            Check(view.Zoom==1.5,"letterboxing preserves aspect ratio");
            view.Layout(new Size(800,600),new Size(400,300));
            Check(view.Zoom==.5,"large image still shrinks");
            view.Fit(); view.Layout(new Size(16,16),new Size(1024,1024));
            view.ZoomAt(.5, new Point(512,512), new Size(16,16), new Size(1024,1024));
            Check(view.Zoom==32,"zoom out from oversized fit is continuous");
            view.Fit(); view.Layout(new Size(16,16),new Size(1024,1024));
            view.ZoomAt(1.2, new Point(512,512), new Size(16,16), new Size(1024,1024));
            Check(view.Zoom==64,"zoom in never unexpectedly shrinks oversized fit");
            return Task.CompletedTask;
        });
        await Test("chosen fit or actual mode follows files and TIFF pages", async () =>
        {
            var window = new MainWindow(new ImageDecoder());
            try
            {
                Layout(window);
                await window.OpenFileAsync(first); window.FitImage(); CheckFit(window);
                await window.NavigateAsync(1); CheckFit(window);
                window.ActualSize(); Check(window.View.Zoom==1 && !window.View.IsFit,"actual selected");
                await window.NavigateAsync(-1); Check(window.View.Zoom==1 && !window.View.IsFit,"actual retained on next image");
                window.ZoomBy(1.2); await window.NavigateAsync(1);
                Check(window.View.Zoom==1 && !window.View.IsFit,"temporary zoom does not replace actual preference");
                window.FitImage(); window.ZoomBy(.8); await window.NavigateAsync(-1); CheckFit(window);
                window.ActualSize();
                await window.OpenFileAsync(Path.Combine(_fixtures,"features","pages.tiff"));
                await window.NavigatePageAsync(1);
                Check(window.View.Zoom==1 && !window.View.IsFit,"actual retained across TIFF pages");
                window.FitImage(); await window.NavigatePageAsync(1); CheckFit(window);
            }
            finally { window.Close(); }
        });
        await Test("view mode saves immediately and survives restart and fullscreen", async () =>
        {
            var settings = Path.Combine(folder,"saved-window-"+Guid.NewGuid().ToString("N")+".json");
            var window = new MainWindow(new ImageDecoder(),settings);
            try
            {
                Layout(window); await window.OpenFileAsync(first); window.ActualSize();
                Check(WindowPlacementStore.Load(settings)?.ViewMode==ImageViewMode.ActualSize,"saved before closing");
                window.ToggleFullscreen();
                Check(!window.View.IsFit && window.View.Zoom==1,"fullscreen keeps actual preference");
                window.ToggleFullscreen();
                Check(!window.View.IsFit && window.View.Zoom==1,"fullscreen exit keeps actual preference");
            }
            finally { window.Close(); }
            var restored = new MainWindow(new ImageDecoder(),settings);
            try
            {
                Layout(restored); await restored.OpenFileAsync(second);
                Check(!restored.View.IsFit && restored.View.Zoom==1,"restart restores actual size");
                restored.ToggleFullscreen(); restored.FitImage();
                Check(WindowPlacementStore.Load(settings)?.ViewMode==ImageViewMode.Fit,"mode change while fullscreen is saved");
            }
            finally { restored.Close(); }
            var fitRestored = new MainWindow(new ImageDecoder(),settings);
            try { Layout(fitRestored); await fitRestored.OpenFileAsync(first); CheckFit(fitRestored); }
            finally { fitRestored.Close(); }
        });
        await Test("legacy settings and unknown view modes fall back to fit", () =>
        {
            var settings = Path.Combine(folder,"legacy-window.json");
            File.WriteAllText(settings,"{\"Left\":100,\"Top\":120,\"Width\":900,\"Height\":700,\"Maximized\":false}");
            Check(WindowPlacementStore.Load(settings)?.ViewMode==ImageViewMode.Fit,"older settings remain compatible");
            File.WriteAllText(settings,"{\"Left\":100,\"Top\":120,\"Width\":900,\"Height\":700,\"Maximized\":false,\"ViewMode\":999}");
            var restored = WindowPlacementStore.Load(settings);
            Check(restored?.ViewMode==ImageViewMode.Fit && restored.Width==900,"invalid mode preserves valid window bounds");
            return Task.CompletedTask;
        });
        await Test("mode changed during decoding applies to the pending image", async () =>
        {
            var decoder = new TransitionDecoder(second);
            var window = new MainWindow(decoder);
            try
            {
                Layout(window); await window.OpenFileAsync(first); window.FitImage();
                var pending = window.NavigateAsync(1);
                await decoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                window.ActualSize(); decoder.Release.TrySetResult(); await pending;
                Check(window.DisplayedPath==second && !window.View.IsFit && window.View.Zoom==1,"latest preference used when image commits");
            }
            finally { decoder.Release.TrySetResult(); window.Close(); }
        });
    }
}
