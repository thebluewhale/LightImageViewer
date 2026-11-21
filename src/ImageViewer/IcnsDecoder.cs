using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace ImageViewer;

internal static class IcnsDecoder
{
    internal static DecodedImage Decode(Stream stream, long bytes, DecodeOptions options, CancellationToken token)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, true);
        uint ReadSize() => BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "icns") throw new InvalidDataException("ICNS 헤더가 손상되었습니다.");
        var end = ReadSize();
        if (end < 8 || end > stream.Length) throw new InvalidDataException("ICNS 길이가 잘못되었습니다.");
        var chunks = new Dictionary<string, (long Offset, int Length)>();
        while (stream.Position < end)
        {
            token.ThrowIfCancellationRequested();
            if (end - stream.Position < 8) throw new InvalidDataException("ICNS 항목이 잘렸습니다.");
            var type = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var length = ReadSize();
            if (length < 8 || stream.Position + length - 8 > end || length > 64 * 1024 * 1024)
                throw new InvalidDataException("ICNS 항목 크기가 잘못되었습니다.");
            chunks[type] = (stream.Position, (int)length - 8);
            stream.Position += length - 8;
        }
        byte[] ReadChunk(string type)
        {
            var chunk = chunks[type]; stream.Position = chunk.Offset;
            return reader.ReadBytes(chunk.Length);
        }
        // Prefer the largest representation; modern chunks contain PNG or JPEG 2000.
        var choices = new (string Type, int Size)[] { ("ic10",1024), ("ic09",512), ("ic14",512), ("ic08",256), ("ic13",256),
            ("ic07",128), ("it32",128), ("ic12",64), ("icp6",64), ("ih32",48), ("ic11",32), ("icp5",32), ("il32",32), ("icp4",16), ("is32",16) };
        foreach (var choice in choices)
        {
            if (!chunks.ContainsKey(choice.Type)) continue;
            var data = ReadChunk(choice.Type);
            BitmapSource bitmap;
            if (choice.Type.EndsWith("32", StringComparison.Ordinal))
            {
                var count = choice.Size * choice.Size;
                var rgba = new byte[count * 4];
                var offset = choice.Type == "it32" ? 4 : 0;
                if (data.Length - offset == count * 3)
                {
                    for (var i = 0; i < count; i++)
                    { rgba[i*4] = data[offset+i*3+2]; rgba[i*4+1] = data[offset+i*3+1]; rgba[i*4+2] = data[offset+i*3]; rgba[i*4+3] = 255; }
                }
                else if (data.Length - offset == count * 4)
                {
                    for (var i = 0; i < count; i++)
                    { rgba[i*4] = data[offset+i*4+3]; rgba[i*4+1] = data[offset+i*4+2]; rgba[i*4+2] = data[offset+i*4+1]; rgba[i*4+3] = 255; }
                }
                else
                {
                    for (var channel = 2; channel >= 0; channel--)
                    {
                        var position = 0;
                        while (position < count)
                        {
                            if (offset >= data.Length) throw new InvalidDataException("ICNS RLE 데이터가 잘렸습니다.");
                            var control = data[offset++];
                            var run = control >= 128 ? control - 125 : control + 1;
                            if (position + run > count || offset + (control >= 128 ? 1 : run) > data.Length)
                                throw new InvalidDataException("ICNS RLE 길이가 잘못되었습니다.");
                            for (var n = 0; n < run; n++) rgba[(position++ * 4) + channel] = data[control >= 128 ? offset : offset++];
                            if (control >= 128) offset++;
                        }
                    }
                    for (var i = 0; i < count; i++) rgba[i*4+3] = 255;
                }
                var maskName = choice.Type switch { "it32" => "t8mk", "ih32" => "h8mk", "il32" => "l8mk", _ => "s8mk" };
                if (chunks.ContainsKey(maskName))
                {
                    var mask = ReadChunk(maskName);
                    if (mask.Length != count) throw new InvalidDataException("ICNS 투명도 마스크 크기가 잘못되었습니다.");
                    for (var i = 0; i < count; i++) rgba[i*4+3] = mask[i];
                }
                bitmap = BitmapSource.Create(choice.Size, choice.Size, 96, 96, PixelFormats.Bgra32, null, rgba, choice.Size*4);
            }
            else
            {
                using var image = new MagickImage();
                var settings = new MagickReadSettings { FrameCount = 1, Format = data.Length >= 8 && data[0] == 137 && data[1] == 80 ? MagickFormat.Png : MagickFormat.Jp2 };
                image.Ping(data, settings);
                if (image.Width > 4096 || image.Height > 4096) throw new InvalidDataException("ICNS 이미지 크기가 너무 큽니다.");
                image.Read(data, settings);
                using var pixels = image.GetPixelsUnsafe();
                var bgra = pixels.ToByteArray(PixelMapping.BGRA)!;
                bitmap = BitmapSource.Create((int)image.Width, (int)image.Height, 96, 96, PixelFormats.Bgra32, null, bgra, (int)image.Width*4);
            }
            var width = bitmap.PixelWidth; var height = bitmap.PixelHeight;
            var edge = ImageDecoder.DisplayEdge(width, height, options.MaxEdge);
            if (Math.Max(width, height) > edge) bitmap = new TransformedBitmap(bitmap, new ScaleTransform((double)edge / Math.Max(width,height), (double)edge / Math.Max(width,height)));
            bitmap.Freeze();
            token.ThrowIfCancellationRequested();
            return new DecodedImage(bitmap, width, height, bytes);
        }
        throw new NotSupportedException("ICNS에 표시 가능한 PNG/JPEG 2000/32비트 RGB 이미지가 없습니다.");
    }
}
