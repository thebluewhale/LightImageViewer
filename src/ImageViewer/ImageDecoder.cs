using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace ImageViewer;

internal sealed record DecodedImage(BitmapSource Bitmap, int Width, int Height, long FileBytes,
    int FrameCount = 1, int PageCount = 1, int PageIndex = 0)
{
    public bool IsPreview => Bitmap.PixelWidth < Width || Bitmap.PixelHeight < Height;
}

internal interface IImageDecoder
{
    Task<DecodedImage> DecodeAsync(string path, CancellationToken cancellationToken);
    Task<DecodedImage> DecodeAsync(string path, DecodeOptions options, CancellationToken token) => DecodeAsync(path, token);
}

internal sealed class ImageDecoder : IImageDecoder
{
    // Bound allocations before decompressing untrusted or accidentally enormous files.
    internal const long MaxPixels = 1_000_000_000;
    internal const long MaxFileBytes = 8L * 1024 * 1024 * 1024;
    internal const long MaxDisplayPixels = 32_000_000;

    internal static int DisplayEdge(int width, int height, int requested)
    {
        var edge = Math.Max(width, height);
        var cap = (int)Math.Max(1, Math.Floor(edge * Math.Min(1, Math.Sqrt((double)MaxDisplayPixels / ((long)width * height)))));
        return Math.Min(16384, requested > 0 ? Math.Min(requested, cap) : cap);
    }

    public Task<DecodedImage> DecodeAsync(string path, CancellationToken token) => DecodeAsync(path, new DecodeOptions(), token);

    public Task<DecodedImage> DecodeAsync(string path, DecodeOptions options, CancellationToken cancellationToken)
    {
        // Called on a worker thread; the window allows only one active decoder.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = stream.Length;
        if (bytes > MaxFileBytes)
            throw new NotSupportedException("파일당 최대 8 GiB까지 지원합니다.");
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".ani")
        {
            using var animation = new AniAnimationSession(path, options.MaxEdge, cancellationToken);
            var first = animation.ReadFrame(0, cancellationToken);
            return Task.FromResult(new DecodedImage(first.Bitmap, animation.Width, animation.Height, bytes, animation.FrameCount));
        }
        if (ExtendedImageDecoder.Supports(extension))
            return Task.FromResult(ExtendedImageDecoder.Decode(stream, extension, bytes, options, cancellationToken));
        byte[] pixels;
        int width, height, orientation;
        int originalWidth, originalHeight, frameCount = 1, pageCount = 1;
        if (Path.GetExtension(path).Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".tiff", StringComparison.OrdinalIgnoreCase) || extension == ".hdp")
        {
            // WIC supplies TIFF support; Skia supplies WebP without OS codec installation.
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            pageCount = decoder.Frames.Count;
            var frame = decoder.Frames[Math.Clamp(options.PageIndex, 0, pageCount - 1)];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            originalWidth = width;
            originalHeight = height;
            CheckDimensions(width, height);
            options = options with { MaxEdge = DisplayEdge(width, height, options.MaxEdge) };
            orientation = 1;
            if (frame.Metadata is BitmapMetadata metadata)
            {
                try
                {
                    if (metadata.GetQuery("/ifd/{ushort=274}") is ushort value) orientation = value;
                }
                catch (NotSupportedException) { }
            }
            BitmapSource source = frame;
            if (options.MaxEdge > 0 && Math.Max(width, height) > options.MaxEdge)
            {
                var scale = (double)options.MaxEdge / Math.Max(width, height);
                source = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                width = source.PixelWidth;
                height = source.PixelHeight;
            }
            pixels = new byte[checked(width * height * 4)];
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(pixels, width * 4, 0);
        }
        else
        {
            using var managed = new SKManagedStream(stream, false);
            using var codec = SKCodec.Create(managed) ?? throw new InvalidDataException("이미지 데이터를 해석할 수 없습니다.");
            width = codec.Info.Width;
            height = codec.Info.Height;
            originalWidth = width;
            originalHeight = height;
            orientation = (int)codec.EncodedOrigin;
            CheckDimensions(width, height);
            frameCount = Math.Max(1, codec.FrameCount);
            // Large still images use the disk-backed decoder instead of allocating an unbounded Skia bitmap.
            if ((long)width * height > MaxDisplayPixels)
            {
                stream.Position = 0;
                return Task.FromResult(ExtendedImageDecoder.Decode(stream, extension, bytes, options, cancellationToken) with { FrameCount = frameCount });
            }
            options = options with { MaxEdge = DisplayEdge(width, height, options.MaxEdge) };
            if (options.MaxEdge > 0 && Math.Max(width, height) > options.MaxEdge)
            {
                var scaled = codec.GetScaledDimensions((float)options.MaxEdge / Math.Max(width, height));
                width = scaled.Width;
                height = scaled.Height;
            }
            using var colorSpace = SKColorSpace.CreateSrgb();
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul, colorSpace);
            using var bitmap = new SKBitmap(info);
            var result = codec.GetPixels(info, bitmap.GetPixels());
            if (result != SKCodecResult.Success)
                throw new InvalidDataException($"이미지 디코딩 실패: {result}");
            cancellationToken.ThrowIfCancellationRequested();
            var bitmapSource = ToSource(bitmap, orientation, options.MaxEdge, cancellationToken);
            return Task.FromResult(new DecodedImage(bitmapSource,
                orientation >= 5 ? originalHeight : originalWidth, orientation >= 5 ? originalWidth : originalHeight,
                bytes, frameCount));
        }
        cancellationToken.ThrowIfCancellationRequested();
        (pixels, width, height) = Orient(pixels, width, height, orientation, cancellationToken);
        var resultSource = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        resultSource.Freeze();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DecodedImage(resultSource,
            orientation >= 5 ? originalHeight : originalWidth, orientation >= 5 ? originalWidth : originalHeight,
            bytes, 1, pageCount, options.PageIndex));
    }

    internal static BitmapSource ToSource(SKBitmap bitmap, int orientation, int maxEdge, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (maxEdge > 0 && Math.Max(bitmap.Width, bitmap.Height) > maxEdge)
        {
            var scale = (double)maxEdge / Math.Max(bitmap.Width, bitmap.Height);
            var info = new SKImageInfo(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale)),
                SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var resized = bitmap.Resize(info, new SKSamplingOptions(SKFilterMode.Linear));
            if (resized is null) throw new InvalidDataException("이미지 크기를 변환할 수 없습니다.");
            return ToSource(resized, orientation, 0, token);
        }
        BitmapSource result;
        if (orientation is >= 2 and <= 8)
        {
            var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
            Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
            var oriented = Orient(pixels, bitmap.Width, bitmap.Height, orientation, token);
            result = BitmapSource.Create(oriented.Width, oriented.Height, 96, 96, PixelFormats.Bgra32, null,
                oriented.Pixels, oriented.Width * 4);
        }
        else
        {
            // WPF copies directly from native memory: no extra full-size managed array.
            result = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgra32, null,
                bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
        }
        result.Freeze();
        token.ThrowIfCancellationRequested();
        return result;
    }

    internal static void CheckDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            throw new NotSupportedException("원본 해상도는 최대 10억 픽셀까지 지원합니다.");
    }

    internal static (byte[] Pixels, int Width, int Height) Orient(byte[] pixels, int width, int height, int orientation, CancellationToken token)
    {
        if (orientation is < 2 or > 8) return (pixels, width, height);
        var newWidth = orientation >= 5 ? height : width;
        var newHeight = orientation >= 5 ? width : height;
        var output = new byte[pixels.Length];
        var inputPixels = MemoryMarshal.Cast<byte, int>(pixels.AsSpan());
        var outputPixels = MemoryMarshal.Cast<byte, int>(output.AsSpan());
        for (var y = 0; y < height; y++)
        {
            if (y % 128 == 0) token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var (dx, dy) = orientation switch
                {
                    2 => (width - 1 - x, y),
                    3 => (width - 1 - x, height - 1 - y),
                    4 => (x, height - 1 - y),
                    5 => (y, x),
                    6 => (height - 1 - y, x),
                    7 => (height - 1 - y, width - 1 - x),
                    8 => (y, width - 1 - x),
                    _ => (x, y)
                };
                outputPixels[dy * newWidth + dx] = inputPixels[y * width + x];
            }
        }
        return (output, newWidth, newHeight);
    }
}
