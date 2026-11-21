using System.IO;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace ImageViewer;

internal sealed record AnimationFrame(BitmapSource Bitmap, int DurationMs);

internal interface IAnimationSession : IDisposable
{
    int FrameCount { get; }
    int RepetitionCount { get; }
    AnimationFrame ReadFrame(int index, CancellationToken token);
}

// Owned by one playback worker. Partial frames/disposal are composited by Skia.
internal sealed class AnimationSession : IAnimationSession
{
    private readonly FileStream _stream;
    private readonly SKManagedStream _managed;
    private readonly SKCodec _codec;
    private readonly SKBitmap _working;
    private readonly SKCodecFrameInfo[] _frames;
    private readonly Dictionary<int, AnimationFrame> _cache = [];
    private readonly bool _cacheAll;
    private int _priorFrame = -1;
    private readonly int _maxEdge;
    public int FrameCount => _frames.Length;
    public int RepetitionCount { get; }

    public AnimationSession(string path, int maxEdge = 0)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            if (_stream.Length > ImageDecoder.MaxFileBytes) throw new NotSupportedException("애니메이션 파일이 너무 큽니다.");
            _managed = new SKManagedStream(_stream, false);
            _codec = SKCodec.Create(_managed) ?? throw new InvalidDataException("애니메이션을 읽을 수 없습니다.");
            ImageDecoder.CheckDimensions(_codec.Info.Width, _codec.Info.Height);
            if ((long)_codec.Info.Width * _codec.Info.Height > ImageDecoder.MaxDisplayPixels)
                throw new NotSupportedException("애니메이션 재생은 프레임당 3,200만 픽셀까지 지원합니다.");
            _frames = _codec.FrameInfo;
            RepetitionCount = _codec.RepetitionCount;
            if (_frames.Length < 2) throw new InvalidDataException("애니메이션 프레임이 없습니다.");
            _maxEdge = maxEdge;
            var size = maxEdge > 0 ? _codec.GetScaledDimensions(Math.Min(1, (float)maxEdge / Math.Max(_codec.Info.Width, _codec.Info.Height)))
                : new SKSizeI(_codec.Info.Width, _codec.Info.Height);
            using var colorSpace = SKColorSpace.CreateSrgb();
            _working = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul, colorSpace));
            var edgeScale = maxEdge > 0 ? Math.Min(1, (double)maxEdge / Math.Max(size.Width, size.Height)) : 1;
            _cacheAll = (double)size.Width * size.Height * 4 * edgeScale * edgeScale * FrameCount <= 64L * 1024 * 1024 && FrameCount <= 512;
        }
        catch
        {
            _working?.Dispose();
            _codec?.Dispose();
            _managed?.Dispose();
            _stream.Dispose();
            throw;
        }
    }

    public AnimationFrame ReadFrame(int index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_cache.TryGetValue(index, out var cached)) return cached;
        var required = _frames[index].RequiredFrame;
        var prior = _priorFrame >= required && _priorFrame < index && _priorFrame >= 0 &&
            _frames[_priorFrame].DisposalMethod != SKCodecAnimationDisposalMethod.RestorePrevious ? _priorFrame : -1;
        if (prior < 0) _working.Erase(SKColors.Transparent);
        var result = _codec.GetPixels(_working.Info, _working.GetPixels(), new SKCodecOptions(index, prior));
        if (result != SKCodecResult.Success) throw new InvalidDataException($"애니메이션 프레임을 읽지 못했습니다: {result}");
        _priorFrame = index;
        var frame = new AnimationFrame(ImageDecoder.ToSource(_working, (int)_codec.EncodedOrigin, _maxEdge, token),
            _frames[index].Duration <= 10 ? 100 : _frames[index].Duration);
        if (_cacheAll) _cache.Add(index, frame);
        return frame;
    }

    public void Dispose()
    {
        _cache.Clear();
        _working.Dispose();
        _codec.Dispose();
        _managed.Dispose();
        _stream.Dispose();
    }
}
