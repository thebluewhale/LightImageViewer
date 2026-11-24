using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace ImageViewer;

public partial class MainWindow : Window
{
    private readonly ImageLoader _loader;
    private readonly string? _settingsPath;
    private CancellationTokenSource? _pending;
    private CancellationTokenSource? _folderPending;
    private CancellationTokenSource? _prefetchPending;
    private CancellationTokenSource? _refinePending;
    private long _folderVersion;
    private long _version;
    private bool _closed;
    private bool _isLoadingImage;
    private string[] _files = [];
    private int _index = -1;
    private DecodedImage? _image;
    private int _pageIndex;
    internal const int LoadingFeedbackDelayMs = 300;

    internal string? SelectedPath => _index >= 0 ? _files[_index] : null;
    internal string? DisplayedPath { get; private set; }
    internal Task PendingOperation { get; private set; } = Task.CompletedTask;
    internal string Status => StateText.Text;
    internal string IndexLabel => IndexText.Text;
    internal string ResolutionLabel => ResolutionText.Text;

    public MainWindow() : this(new ImageDecoder(), WindowPlacementStore.DefaultPath) { }

    internal MainWindow(IImageDecoder decoder, string? settingsPath = null)
    {
        _loader = new ImageLoader(decoder);
        _settingsPath = settingsPath;
        InitializeComponent();
        RestorePlacement();
        UpdateToolbar();
        Closing += (_, _) => SavePlacement();
        Closed += (_, _) =>
        {
            _closed = true;
            ++_version;
            _pending?.Cancel();
            _folderPending?.Cancel();
            _prefetchPending?.Cancel();
            _refinePending?.Cancel();
            StopAnimation();
            _loader.Clear();
            Picture.Source = null;
            _image = null;
        };
    }

    public Task OpenFileAsync(string path) => PendingOperation = OpenCoreAsync(path);

    private async Task OpenCoreAsync(string path)
    {
        if (_closed) return;
        _folderPending?.Cancel();
        using var folderRequest = new CancellationTokenSource();
        _folderPending = folderRequest;
        var folderVersion = ++_folderVersion;
        try
        {
            path = Path.GetFullPath(path);
            if (!ImageCatalog.IsSupported(path)) throw new NotSupportedException("지원하는 이미지 파일을 선택해 주세요. 파일 열기 창에서 지원 확장자를 확인할 수 있습니다.");
            _files = [path];
            IndexText.ToolTip = null;
            _index = 0;
            _pageIndex = 0;
            // Display the selected image without waiting for a large folder to sort.
            var catalogTask = Task.Run(() => ImageCatalog.Open(path, folderRequest.Token), folderRequest.Token);
            var imageTask = LoadSelectedAsync();
            IndexText.Text = "1/…";
            try
            {
                var catalog = await catalogTask;
                if (_closed || folderVersion != _folderVersion) return;
                var selected = SelectedPath;
                _files = catalog.Files;
                _index = Array.FindIndex(_files, file => string.Equals(file, selected, StringComparison.OrdinalIgnoreCase));
                if (_index < 0) _index = catalog.SelectedIndex;
                IndexText.Text = $"{_index + 1}/{_files.Length}";
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!_closed && folderVersion == _folderVersion)
                {
                    ErrorLog.Write(error);
                    IndexText.Text = "1/1";
                    IndexText.ToolTip = "폴더 목록을 읽지 못했습니다. 현재 파일만 표시합니다.";
                }
            }
            await imageTask;
            if (!_closed && folderVersion == _folderVersion) SchedulePrefetch();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error)
        {
            if (!_closed && folderVersion == _folderVersion)
            {
                _pending?.Cancel();
                ++_version;
                _isLoadingImage = false;
                _files = [];
                _index = -1;
                IndexText.Text = "0/0";
                ShowFailure(error);
            }
        }
        finally { if (ReferenceEquals(_folderPending, folderRequest)) _folderPending = null; }
    }

    internal Task NavigateAsync(int delta)
    {
        if (_closed || _files.Length == 0) return Task.CompletedTask;
        _index = ImageCatalog.Wrap(_index, delta, _files.Length);
        _pageIndex = 0;
        return PendingOperation = LoadSelectedAsync();
    }

    internal Task NavigateBoundaryAsync(bool last)
    {
        if (_closed || _files.Length == 0) return Task.CompletedTask;
        _index = last ? _files.Length - 1 : 0;
        _pageIndex = 0;
        return PendingOperation = LoadSelectedAsync();
    }

    internal Task NavigatePageAsync(int delta)
    {
        if (_isLoadingImage || _image is not { PageCount: > 1 }) return Task.CompletedTask;
        _pageIndex = Math.Clamp(_pageIndex + delta, 0, _image.PageCount - 1);
        return PendingOperation = LoadSelectedAsync();
    }

    private async Task LoadSelectedAsync()
    {
        using var request = BeginRequest(out var version);
        _prefetchPending?.Cancel();
        _refinePending?.Cancel();
        var path = SelectedPath!;
        IndexText.Text = $"{_index + 1}/{_files.Length}";
        // Keep the last rendered image and its geometry until a replacement is ready.
        StopAnimation();
        EndPan();
        UpdateToolbar();
        StateText.Text = "";
        WelcomePanel.Visibility = MessagePanel.Visibility = Visibility.Collapsed;
        using var feedbackRequest = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
        var feedbackTask = ShowLoadingFeedbackAsync(path, version, feedbackRequest.Token);
        try
        {
            // Coalesce key repeats. The gate bounds memory even if a codec is slow to cancel.
            await Task.Delay(45, request.Token);
            var image = await _loader.LoadAsync(path, new DecodeOptions(PreviewEdge, _pageIndex), request.Token);
            if (!IsCurrent(version)) return;
            _isLoadingImage = false;
            _image = image;
            DisplayedPath = path;
            Title = Path.GetFileName(path) + " — LightImageViewer";
            ToolTip = path;
            Picture.Source = image.Bitmap;
            ResolutionText.Text = $"{image.Width} × {image.Height}";
            FileSizeText.Text = (image.FileBytes / 1_000_000d).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
            FileSizeText.ToolTip = image.FileBytes.ToString("N0", CultureInfo.CurrentCulture) + " bytes (1 MB = 1,000,000 bytes)";
            ResolutionText.ToolTip = "사진 방향을 반영한 원본 픽셀 크기";
            StateText.Text = "";
            WelcomePanel.Visibility = MessagePanel.Visibility = Visibility.Collapsed;
            ImageFrame.Visibility = Visibility.Visible;
            ApplyPreferredViewMode();
            UpdateImageSize();
            UpdateToolbar();
            if (image.FrameCount > 1) StartAnimation(path, version, PreviewEdge);
            RequestRefinement();
            SchedulePrefetch();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsCurrent(version)) ShowFailure(error);
        }
        finally
        {
            feedbackRequest.Cancel();
            await feedbackTask;
            EndRequest(request);
        }
    }

    private async Task ShowLoadingFeedbackAsync(string path, long version, CancellationToken token)
    {
        try
        {
            await Task.Delay(LoadingFeedbackDelayMs, token);
            if (!IsCurrent(version) || token.IsCancellationRequested) return;
            StateText.Text = "불러오는 중…";
            // Existing images remain unobscured; the first open still has a central hint.
            if (_image is null)
                ShowMessage("이미지를 불러오는 중…", Path.GetFileName(path), false);
        }
        catch (OperationCanceledException) { }
    }

    private CancellationTokenSource BeginRequest(out long version)
    {
        _pending?.Cancel();
        version = ++_version;
        _isLoadingImage = true;
        return _pending = new CancellationTokenSource();
    }

    private bool IsCurrent(long version) => !_closed && version == _version;
    private void EndRequest(CancellationTokenSource request)
    {
        if (ReferenceEquals(_pending, request))
        {
            _pending = null;
            _isLoadingImage = false;
            UpdateToolbar();
        }
    }

    private void ClearImage()
    {
        StopAnimation();
        EndPan();
        Picture.Source = null;
        _image = null;
        DisplayedPath = null;
        ImageFrame.Visibility = Visibility.Collapsed;
        ResolutionText.Text = "— × —";
        ResolutionText.ToolTip = null;
        FileSizeText.Text = "— MB";
        FileSizeText.ToolTip = null;
        UpdateToolbar();
    }

    private void ShowMessage(string title, string detail, bool canOpen)
    {
        WelcomePanel.Visibility = Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Visible;
        MessageTitle.Text = title;
        MessageDetail.Text = detail;
        RecoveryButton.Visibility = canOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowFailure(Exception error)
    {
        ErrorLog.Write(error);
        ClearImage();
        Title = SelectedPath is { } path ? Path.GetFileName(path) + " — LightImageViewer" : "LightImageViewer";
        ToolTip = SelectedPath;
        StateText.Text = "읽기 실패";
        var explanation = error switch
        {
            FileNotFoundException or DirectoryNotFoundException => "파일이 이동 또는 삭제되었습니다.",
            UnauthorizedAccessException => "파일 또는 폴더에 접근할 권한이 없습니다.",
            NotSupportedException => error.Message,
            OutOfMemoryException => "이미지를 표시할 메모리가 부족합니다. 다른 파일을 열어 주세요.",
            IOException => "파일을 읽을 수 없습니다. 다른 프로그램에서 사용 중이거나 저장 장치에 문제가 있을 수 있습니다.",
            _ => "손상되었거나 지원하지 않는 이미지 데이터입니다."
        };
        var recovery = _files.Length > 0
            ? "\n\n방향키 / Page Up / Page Down으로 다른 이미지를 볼 수 있습니다.\nCtrl+O로 다른 파일을 열 수 있습니다."
            : "\n\nCtrl+O 또는 아래 버튼으로 이미지 파일을 선택해 주세요.";
        ShowMessage("이미지를 열 수 없습니다", explanation + recovery, true);
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateImageSize();
        RequestRefinement();
    }

    private void UpdateImageSize()
    {
        if (_image is null) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var width = _image.Width / dpi.DpiScaleX;
        var height = _image.Height / dpi.DpiScaleY;
        _view.Layout(new Size(width, height), Viewport.RenderSize);
        ImageFrame.Width = width * _view.Zoom;
        ImageFrame.Height = height * _view.Zoom;
        Canvas.SetLeft(ImageFrame, (Viewport.ActualWidth - ImageFrame.Width) / 2 + _view.Offset.X);
        Canvas.SetTop(ImageFrame, (Viewport.ActualHeight - ImageFrame.Height) / 2 + _view.Offset.Y);
        ZoomText.Text = (_view.Zoom * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        Viewport.Cursor = ImageFrame.Width > Viewport.ActualWidth || ImageFrame.Height > Viewport.ActualHeight ? Cursors.Hand : Cursors.Arrow;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateImageSize();
        RequestRefinement();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => ChooseFile();

    private void ChooseFile()
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "이미지 열기",
                Filter = "이미지 파일|" + string.Join(';', ImageCatalog.Extensions.Select(ext => "*" + ext)) + "|모든 파일|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (SelectedPath is { } selected) dialog.InitialDirectory = Path.GetDirectoryName(selected);
            if (dialog.ShowDialog(this) == true) _ = OpenFileAsync(dialog.FileName);
        }
        catch (Exception error)
        {
            ErrorLog.Write(error);
            MessageBox.Show(this, "파일 선택 창을 열지 못했습니다. 이미지를 창으로 끌어 놓아 주세요.", "LightImageViewer");
        }
    }

    internal static int NavigationDelta(Key key) => key switch
    {
        Key.Right or Key.Down or Key.Next => 1,
        Key.Left or Key.Up or Key.Prior => -1,
        _ => 0
    };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleViewKey(e)) return;
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (!e.IsRepeat) ChooseFile();
        }
        else if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (!e.IsRepeat) ShowHelp();
        }
        else if (Keyboard.Modifiers == ModifierKeys.None && NavigationDelta(e.Key) is var delta && delta != 0)
        {
            e.Handled = true;
            _ = NavigateAsync(delta);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
                _ = OpenFileAsync(paths.FirstOrDefault(ImageCatalog.IsSupported) ?? paths[0]);
        }
        catch (Exception error)
        {
            ErrorLog.Write(error);
            MessageBox.Show(this, "드롭한 파일 정보를 읽을 수 없습니다. Ctrl+O로 파일을 열어 주세요.", "LightImageViewer");
        }
    }

    private void ShowHelp()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().First(n => n.EndsWith("THIRD-PARTY-NOTICES.txt", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        var content = "LightImageViewer 0.3.6\n\nCtrl+O: 파일 열기 / 파일을 창에 끌어 놓아도 열 수 있습니다.\n" +
            "→ ↓ Page Down: 다음 / ← ↑ Page Up: 이전\n첫 파일과 마지막 파일은 서로 이어집니다.\n" +
            "Home / End: 첫 파일 / 마지막 파일\n휠 위: 이전 이미지 / 휠 아래: 다음 이미지\n" +
            "Ctrl+휠 또는 + / -: 확대·축소, 드래그: 이미지 이동\n" +
            "1: 원본 크기 / F: 창에 맞추기 / F11: 전체화면 / Esc: 복귀\n" +
            "Space: 애니메이션 재생·정지 / [ ]: TIFF 페이지 이동\n" +
            "창 위치·크기·최대화 상태와 창 맞춤/원본 크기 보기 모드를 기억합니다. GIF·WebP·ANI 애니메이션을 자동 재생합니다.\n" +
            "PSD/PSB는 저장된 합성 이미지를 표시합니다.\n" +
            "파일 8 GiB · 원본 10억 픽셀 이하 / 표시 데이터 3,200만 픽셀·긴 변 16,384px 이하\n" +
            "대형 이미지는 표시 해상도를 제한합니다. MB는 1,000,000 bytes입니다.\n" +
            "기본 앱 연결은 Windows의 연결 프로그램에서 이 EXE를 선택하세요.\n\n" + reader.ReadToEnd();
        new Window
        {
            Owner = this, Title = "LightImageViewer — 도움말 및 라이선스", Width = 720, Height = 540,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox { Text = content, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(20) }
        }.ShowDialog();
    }
}
