using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace ImageViewer;

// RIFF ACON: icon chunks are frames; seq/rate chunks describe playback steps in 1/60 seconds.
internal sealed class AniAnimationSession : IAnimationSession
{
    private readonly FileStream _stream;
    private readonly List<(long Offset, int Length)> _icons = [];
    private readonly Dictionary<int, BitmapSource> _cache = [];
    private long _cacheBytes;
    private readonly int _maxEdge;
    private readonly CancellationToken _token;
    private uint[]? _sequence, _rates;
    private uint _frames, _steps, _rate, _flags;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FrameCount => (int)_steps;
    public int RepetitionCount => -1;

    internal AniAnimationSession(string path, int maxEdge = 0, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        _token = token;
        _maxEdge = maxEdge;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            if (_stream.Length > ImageDecoder.MaxFileBytes) throw new NotSupportedException("파일당 최대 8 GiB까지 지원합니다.");
            using var reader = new BinaryReader(_stream, Encoding.ASCII, true);
            if (FourCC(reader) != "RIFF") throw Invalid();
            var end = 8L + reader.ReadUInt32();
            if (FourCC(reader) != "ACON" || end > _stream.Length || end < 12) throw Invalid();
            ReadChunks(reader, end, false);
            if (_frames == 0 || _frames != _icons.Count || _steps == 0 || _steps > 65536 || (_flags & 1) == 0)
                throw Invalid();
            if ((_flags & 2) != 0 && (_sequence is null || _sequence.Length != _steps)) throw Invalid();
            if (_rates is not null && _rates.Length != _steps) throw Invalid();
            for (var i = 0; i < _steps; i++)
                if (FrameFor(i) >= _frames) throw Invalid();
            // Inspect headers only. Pixel frames are decoded lazily and cached within 64 MiB.
            for (var i = 0; i < _icons.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                using var data = SKData.CreateCopy(ReadIcon(i));
                using var codec = SKCodec.Create(data) ?? throw Invalid();
                Width = Math.Max(Width, codec.Info.Width);
                Height = Math.Max(Height, codec.Info.Height);
            }
            if (Width > 4096 || Height > 4096) throw new NotSupportedException("ANI 프레임은 최대 4096 × 4096까지 지원합니다.");
            ImageDecoder.CheckDimensions(Width, Height);
        }
        catch { _stream.Dispose(); throw; }
    }

    private static InvalidDataException Invalid() => new("ANI 프레임 또는 재생 정보가 손상되었거나 지원하지 않는 구성입니다.");
    private static string FourCC(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
    private void ReadChunks(BinaryReader reader, long end, bool frames)
    {
        while (_stream.Position < end)
        {
            _token.ThrowIfCancellationRequested();
            if (end - _stream.Position < 8) throw Invalid();
            var type = FourCC(reader);
            var size = reader.ReadUInt32();
            var start = _stream.Position;
            var next = start + size;
            if (next > end || next + (size & 1) > end) throw Invalid();
            if (!frames && type == "anih")
            {
                if (size < 36 || _frames != 0 || reader.ReadUInt32() != 36) throw Invalid();
                _frames = reader.ReadUInt32(); _steps = reader.ReadUInt32();
                _stream.Position += 16;
                _rate = reader.ReadUInt32(); _flags = reader.ReadUInt32();
            }
            else if (!frames && type is "seq " or "rate")
            {
                if (size % 4 != 0 || size > 65536 * 4) throw Invalid();
                var values = new uint[size / 4];
                for (var i = 0; i < values.Length; i++) values[i] = reader.ReadUInt32();
                if (type == "seq ") _sequence = values; else _rates = values;
            }
            else if (!frames && type == "LIST" && size >= 4 && FourCC(reader) == "fram")
                ReadChunks(reader, next, true);
            else if (frames && type == "icon")
            {
                if (size < 22 || size > 16 * 1024 * 1024 || _icons.Count >= 4096) throw Invalid();
                _icons.Add((start, (int)size));
            }
            _stream.Position = next + (size & 1);
        }
    }

    private int FrameFor(int step) => (_flags & 2) != 0 ? checked((int)_sequence![step]) : step % _icons.Count;
    private byte[] ReadIcon(int index)
    {
        var (offset, length) = _icons[index];
        _stream.Position = offset;
        var data = new byte[length];
        _stream.ReadExactly(data);
        // CUR and ICO share the image payload. Replace cursor hotspots with icon directory metadata.
        if (data[0] != 0 || data[1] != 0 || data[3] != 0 || data[2] is not (1 or 2)) throw Invalid();
        var count = BitConverter.ToUInt16(data, 4);
        if (count == 0 || 6L + 16L * count > data.Length) throw Invalid();
        if (data[2] == 2)
        {
            data[2] = 1;
            for (var i = 0; i < count; i++)
            { var p = 6 + i * 16 + 4; data[p] = 1; data[p + 1] = data[p + 2] = data[p + 3] = 0; }
        }
        return data;
    }

    public AnimationFrame ReadFrame(int index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var frameIndex = FrameFor(index);
        if (!_cache.TryGetValue(frameIndex, out var bitmap))
        {
            using var data = SKData.CreateCopy(ReadIcon(frameIndex));
            using var codec = SKCodec.Create(data) ?? throw Invalid();
            using var decoded = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success) throw Invalid();
            using var canvas = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            canvas.Erase(SKColors.Transparent);
            using (var paint = new SKCanvas(canvas)) paint.DrawBitmap(decoded, (Width - decoded.Width) / 2, (Height - decoded.Height) / 2, new SKSamplingOptions(SKFilterMode.Nearest));
            bitmap = ImageDecoder.ToSource(canvas, 1, ImageDecoder.DisplayEdge(Width, Height, _maxEdge), token);
            var cost = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
            if (_cacheBytes + cost > 64L * 1024 * 1024) { _cache.Clear(); _cacheBytes = 0; }
            if (cost <= 64L * 1024 * 1024) { _cache[frameIndex] = bitmap; _cacheBytes += cost; }
        }
        var ticks = _rates is null ? _rate : _rates[index];
        var duration = ticks == 0 ? 100 : (int)Math.Clamp(((long)ticks * 1000 + 30) / 60, 1, int.MaxValue);
        return new AnimationFrame(bitmap, duration);
    }

    public void Dispose() { _cache.Clear(); _stream.Dispose(); }
}
