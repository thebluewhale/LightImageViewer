using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ImageViewer;

public partial class MainWindow
{
    private readonly ViewTransform _view = new();
    private Point? _dragPoint;
    private WindowPlacement? _beforeFullscreen;
    internal bool IsFullscreen { get; private set; }
    internal ViewTransform View => _view;
    internal Task PendingRefinement { get; private set; } = Task.CompletedTask;
    private int PreviewEdge
    {
        get
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return Math.Clamp((int)Math.Ceiling(Math.Max(Viewport.ActualWidth * dpi.DpiScaleX,
                Viewport.ActualHeight * dpi.DpiScaleY) / 512) * 512, 1536, 4096);
        }
    }

    private Size LogicalImageSize
    {
        get
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return _image is null ? default : new Size(_image.Width / dpi.DpiScaleX, _image.Height / dpi.DpiScaleY);
        }
    }

    internal void ZoomBy(double factor, Point? anchor = null)
    {
        if (_image is null) return;
        _view.ZoomAt(factor, anchor ?? new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2), LogicalImageSize, Viewport.RenderSize);
        UpdateImageSize();
        RequestRefinement();
    }

    internal void FitImage() { _view.Fit(); UpdateImageSize(); RequestRefinement(); }
    internal void ActualSize() { _view.Actual(); UpdateImageSize(); RequestRefinement(); }
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.2);
    private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomBy(1.2);
    private void OnActualClick(object sender, RoutedEventArgs e) => ActualSize();
    private void OnFitClick(object sender, RoutedEventArgs e) => FitImage();
    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void OnPreviousPageClick(object sender, RoutedEventArgs e) => _ = NavigatePageAsync(-1);
    private void OnNextPageClick(object sender, RoutedEventArgs e) => _ = NavigatePageAsync(1);

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            ZoomBy(Math.Pow(1.2, e.Delta / 120.0), e.GetPosition(Viewport));
        else
            // Navigation remains available while loading or displaying a failed image.
            _ = NavigateAsync(e.Delta > 0 ? -1 : 1);
    }

    private void OnPanStart(object sender, MouseButtonEventArgs e)
    {
        if (_image is null || (ImageFrame.Width <= Viewport.ActualWidth && ImageFrame.Height <= Viewport.ActualHeight)) return;
        _dragPoint = e.GetPosition(Viewport);
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void OnPanMove(object sender, MouseEventArgs e)
    {
        if (_dragPoint is not { } previous) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndPan(); return; }
        var point = e.GetPosition(Viewport);
        _view.Pan(point - previous, LogicalImageSize, Viewport.RenderSize);
        _dragPoint = point;
        UpdateImageSize();
        e.Handled = true;
    }

    private void OnPanEnd(object sender, MouseButtonEventArgs e) => EndPan();
    private void OnPanCaptureLost(object sender, MouseEventArgs e) => _dragPoint = null;
    private void EndPan() { _dragPoint = null; if (Viewport?.IsMouseCaptured == true) Viewport.ReleaseMouseCapture(); }

    private bool HandleViewKey(KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None && !(e.Key == Key.OemPlus && Keyboard.Modifiers == ModifierKeys.Shift)) return false;
        switch (e.Key)
        {
            case Key.Escape: if (!IsFullscreen) return false; ToggleFullscreen(); break;
            case Key.F11: if (!e.IsRepeat) ToggleFullscreen(); break;
            case Key.Home: _ = NavigateBoundaryAsync(false); break;
            case Key.End: _ = NavigateBoundaryAsync(true); break;
            case Key.Add: case Key.OemPlus: ZoomBy(1.2); break;
            case Key.Subtract: case Key.OemMinus: ZoomBy(1 / 1.2); break;
            case Key.D1: case Key.NumPad1: ActualSize(); break;
            case Key.F: FitImage(); break;
            case Key.Space: if (!e.IsRepeat) TogglePlayback(); break;
            case Key.OemOpenBrackets: _ = NavigatePageAsync(-1); break;
            case Key.OemCloseBrackets: _ = NavigatePageAsync(1); break;
            default: return false;
        }
        e.Handled = true;
        return true;
    }

    internal void ToggleFullscreen()
    {
        EndPan();
        if (!IsFullscreen)
        {
            _beforeFullscreen = CapturePlacement();
            IsFullscreen = true;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Toolbar.Visibility = Statusbar.Visibility = Visibility.Collapsed;
            // Native physical monitor bounds include the taskbar area on the current monitor.
            if (IsLoaded) NativeWindow.CoverMonitor(this);
            FitImage();
        }
        else
        {
            IsFullscreen = false;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Toolbar.Visibility = Statusbar.Visibility = Visibility.Visible;
            if (_beforeFullscreen is { } saved) ApplyPlacement(saved);
            UpdateImageSize();
        }
    }

    private WindowPlacement CapturePlacement()
    {
        if (IsFullscreen && _beforeFullscreen is { } saved) return saved;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            WindowState == WindowState.Maximized || (WindowState == WindowState.Minimized && _lastNonMinimizedState == WindowState.Maximized));
    }

    private WindowState _lastNonMinimizedState;
    private void RestorePlacement()
    {
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _lastNonMinimizedState = WindowState;
        };
        if (_settingsPath is not null && WindowPlacementStore.Load(_settingsPath) is { } saved)
            ApplyPlacement(NativeWindow.KeepVisible(saved));
    }

    private void ApplyPlacement(WindowPlacement saved)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = saved.Left; Top = saved.Top; Width = saved.Width; Height = saved.Height;
        WindowState = saved.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    private void SavePlacement()
    {
        if (_settingsPath is not null) WindowPlacementStore.Save(_settingsPath, CapturePlacement());
    }

    private void UpdateToolbar()
    {
        if (ZoomInButton is null) return;
        ZoomInButton.IsEnabled = ZoomOutButton.IsEnabled = ActualButton.IsEnabled = FitButton.IsEnabled = _image is not null;
        if (_image is null) ZoomText.Text = "—";
        PlayButton.Visibility = _image is { FrameCount: > 1 } ? Visibility.Visible : Visibility.Collapsed;
        PageControls.Visibility = _image is { PageCount: > 1 } ? Visibility.Visible : Visibility.Collapsed;
        PlayButton.IsEnabled = PageControls.IsEnabled = !_isLoadingImage;
        if (_image is not null) PageText.Text = $"{_image.PageIndex + 1}/{_image.PageCount} 쪽";
    }

    private void RequestRefinement()
    {
        if (_closed || _isLoadingImage || _image is not { IsPreview: true } image || DisplayedPath is not { } path) return;
        var availableScale = (double)image.Bitmap.PixelWidth / image.Width;
        if (_view.Zoom <= availableScale * 1.05) return;
        var limit = ImageDecoder.DisplayEdge(image.Width, image.Height, 0);
        if (Math.Max(image.Bitmap.PixelWidth, image.Bitmap.PixelHeight) >= limit - 1) return;
        _refinePending?.Cancel();
        var request = new CancellationTokenSource();
        _refinePending = request;
        PendingRefinement = RefineAsync(path, _version, image.PageIndex, request);
    }

    private async Task RefineAsync(string path, long version, int page, CancellationTokenSource request)
    {
        using (request)
        {
            try
            {
                await Task.Delay(160, request.Token);
                var image = await _loader.LoadAsync(path, new DecodeOptions(0, page), request.Token);
                if (!IsCurrent(version) || request.IsCancellationRequested) return;
                _image = image;
                Picture.Source = image.Bitmap;
                StateText.Text = image.IsPreview ? "대형 이미지 · 표시 해상도 제한 적용" : "";
                if (image.FrameCount > 1) StartAnimation(path, version, 0, _paused);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (IsCurrent(version) && !request.IsCancellationRequested)
                { ErrorLog.Write(error); StateText.Text = "원본 읽기 실패 · 미리보기 표시"; }
            }
            finally { if (ReferenceEquals(_refinePending, request)) _refinePending = null; }
        }
    }

    private void SchedulePrefetch()
    {
        if (_closed || _isLoadingImage || _image is null || _files.Length <= 1) return;
        _prefetchPending?.Cancel();
        var request = new CancellationTokenSource();
        _prefetchPending = request;
        var paths = new[] { _files[ImageCatalog.Wrap(_index, 1, _files.Length)], _files[ImageCatalog.Wrap(_index, -1, _files.Length)] }.Distinct().ToArray();
        _ = PrefetchAsync(paths, PreviewEdge, request);
    }

    private async Task PrefetchAsync(string[] paths, int edge, CancellationTokenSource request)
    {
        using (request)
        {
            try
            {
                await Task.Delay(200, request.Token);
                foreach (var path in paths)
                {
                    try { await _loader.LoadAsync(path, new DecodeOptions(edge), request.Token); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* A bad neighbor is reported only when actually selected. */ }
                }
            }
            catch (OperationCanceledException) { }
            finally { if (ReferenceEquals(_prefetchPending, request)) _prefetchPending = null; }
        }
    }
}
