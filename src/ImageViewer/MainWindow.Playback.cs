using System.Windows;

namespace ImageViewer;

public partial class MainWindow
{
    private CancellationTokenSource? _animationPending;
    private bool _paused;
    private bool _animationEnded;
    internal int AnimationFrameIndex { get; private set; }
    internal bool IsPlaying => !_isLoadingImage && _image is { FrameCount: > 1 } && !_paused && !_animationEnded;
    internal Task AnimationTask { get; private set; } = Task.CompletedTask;

    private void StartAnimation(string path, long version, int edge, bool paused = false)
    {
        StopAnimation();
        _paused = paused;
        _animationEnded = false;
        PlayIcon.Text = paused ? "▶" : "Ⅱ";
        var request = new CancellationTokenSource();
        _animationPending = request;
        AnimationTask = PlayAsync(path, version, edge, request);
    }

    private async Task PlayAsync(string path, long version, int edge, CancellationTokenSource request)
    {
        using (request)
        {
            IAnimationSession? session = null;
            Task<AnimationFrame>? nextFrame = null;
            try
            {
                session = await Task.Run<IAnimationSession>(() => System.IO.Path.GetExtension(path).Equals(".ani", StringComparison.OrdinalIgnoreCase)
                    ? new AniAnimationSession(path, edge, request.Token) : new AnimationSession(path, edge), request.Token);
                var frame = await Task.Run(() => session.ReadFrame(0, request.Token), request.Token);
                var index = 0;
                var loops = 0;
                while (IsCurrent(version) && !request.IsCancellationRequested)
                {
                    while (_paused || WindowState == WindowState.Minimized) await Task.Delay(50, request.Token);
                    request.Token.ThrowIfCancellationRequested();
                    if (!IsCurrent(version)) return;
                    Picture.Source = frame.Bitmap;
                    AnimationFrameIndex = index;
                    var next = index + 1;
                    if (next == session.FrameCount)
                    {
                        if (session.RepetitionCount >= 0 && loops >= session.RepetitionCount)
                        {
                            await Task.Delay(frame.DurationMs, request.Token);
                            _animationEnded = true;
                            PlayIcon.Text = "▶";
                            return;
                        }
                        loops++;
                        next = 0;
                    }
                    // Decode one frame ahead while the current frame's duration elapses.
                    nextFrame = Task.Run(() => session.ReadFrame(next, request.Token), request.Token);
                    await Task.WhenAll(nextFrame, Task.Delay(frame.DurationMs, request.Token));
                    frame = await nextFrame;
                    nextFrame = null;
                    index = next;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (IsCurrent(version) && !request.IsCancellationRequested)
                {
                    ErrorLog.Write(error);
                    StateText.Text = "애니메이션 읽기 실패 · 정지 화면 표시";
                    _animationEnded = true;
                    PlayIcon.Text = "▶";
                }
            }
            finally
            {
                // Native decode must finish before the codec/stream is disposed.
                if (nextFrame is not null) { try { await nextFrame; } catch { } }
                session?.Dispose();
                if (ReferenceEquals(_animationPending, request)) _animationPending = null;
            }
        }
    }

    private void StopAnimation()
    {
        _animationPending?.Cancel();
        _paused = false;
        _animationEnded = false;
        AnimationFrameIndex = 0;
    }

    internal void TogglePlayback()
    {
        if (_isLoadingImage || _image is not { FrameCount: > 1 } || DisplayedPath is not { } path) return;
        if (_animationEnded) StartAnimation(path, _version, _image.IsPreview ? PreviewEdge : 0);
        else { _paused = !_paused; PlayIcon.Text = _paused ? "▶" : "Ⅱ"; }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => TogglePlayback();
}
