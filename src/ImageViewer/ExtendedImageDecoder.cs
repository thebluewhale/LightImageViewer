using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace ImageViewer;

internal static class ExtendedImageDecoder
{
    static ExtendedImageDecoder()
    {
        // ImageMagick spills large pixel caches to disk instead of requiring all pixels in RAM.
        ResourceLimits.Memory = 256UL * 1024 * 1024;
        ResourceLimits.MaxMemoryRequest = 128UL * 1024 * 1024;
        ResourceLimits.MaxProfileSize = 32UL * 1024 * 1024;
        ResourceLimits.Disk = 8UL * 1024 * 1024 * 1024;
        ResourceLimits.Thread = 2;
        ResourceLimits.Width = 300_000;
        ResourceLimits.Height = 300_000;
        ResourceLimits.ListLength = 1024;
    }

    internal static bool Supports(string extension) => extension is
        ".avif" or ".cur" or ".emf" or ".icns" or ".pbm" or ".pcx" or ".pgm" or
        ".ppm" or ".psb" or ".psd" or ".ras" or ".tga" or ".wbmp" or ".wmf";

    internal static DecodedImage Decode(Stream stream, string extension, long bytes, DecodeOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (extension == ".icns") return IcnsDecoder.Decode(stream, bytes, options, token);
        // Explicit coder selection also handles formats such as TGA/WBMP without a unique signature.
        var format = extension switch
        {
            ".avif" => MagickFormat.Avif, ".cur" => MagickFormat.Cur,
            ".emf" => MagickFormat.Emf, ".wmf" => MagickFormat.Wmf,
            ".pbm" => MagickFormat.Pbm,
            ".pcx" => MagickFormat.Pcx, ".pgm" => MagickFormat.Pgm,
            ".ppm" => MagickFormat.Ppm, ".psb" => MagickFormat.Psb,
            ".psd" => MagickFormat.Psd, ".ras" => MagickFormat.Ras,
            ".tga" => MagickFormat.Tga, ".wbmp" => MagickFormat.Wbmp,
            ".png" => MagickFormat.Png, ".bmp" => MagickFormat.Bmp,
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => MagickFormat.Jpeg,
            ".webp" => MagickFormat.WebP, ".gif" => MagickFormat.Gif,
            _ => throw new NotSupportedException("지원하지 않는 이미지 형식입니다.")
        };
        var settings = new MagickReadSettings { Format = format, FrameIndex = 0, FrameCount = 1 };
        // PSD/PSB scene zero is the saved composite. Do not decode or flatten individual layers.
        settings.SetDefine(MagickFormat.Psd, "skip-layers", true);
        using var image = new MagickImage();
        image.Progress += (_, progress) => progress.Cancel = token.IsCancellationRequested;
        try
        {
            image.Ping(stream, settings);
            var width = checked((int)image.Width);
            var height = checked((int)image.Height);
            ImageDecoder.CheckDimensions(width, height);
            var swap = (int)image.Orientation is >= 5 and <= 8;
            stream.Position = 0;
            token.ThrowIfCancellationRequested();
            image.Read(stream, settings);
            image.AutoOrient();
            var edge = ImageDecoder.DisplayEdge(width, height, options.MaxEdge);
            if (Math.Max(image.Width, image.Height) > edge)
                image.Resize((uint)edge, (uint)edge);
            image.ColorSpace = ColorSpace.sRGB;
            using var pixels = image.GetPixelsUnsafe();
            var data = pixels.ToByteArray(PixelMapping.BGRA) ?? throw new InvalidDataException("이미지 픽셀을 읽을 수 없습니다.");
            token.ThrowIfCancellationRequested();
            var bitmap = BitmapSource.Create((int)image.Width, (int)image.Height, 96, 96, PixelFormats.Bgra32, null, data, checked((int)image.Width * 4));
            bitmap.Freeze();
            return new DecodedImage(bitmap, swap ? height : width, swap ? width : height, bytes);
        }
        catch (MagickException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (MagickException error) when (extension is ".psd" or ".psb")
        {
            throw new NotSupportedException("PSD/PSB 합성 이미지를 읽을 수 없습니다. Photoshop에서 호환성 최대화로 다시 저장해 주세요. 파일 손상 또는 메모리·임시 디스크 부족도 확인해 주세요.", error);
        }
    }
}
