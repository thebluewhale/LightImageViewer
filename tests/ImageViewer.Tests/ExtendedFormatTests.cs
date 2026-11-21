using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using ImageViewer;

internal static partial class Program
{
    private static async Task RunExtendedFormatTests()
    {
        var folder = Path.Combine(_fixtures, "extended");
        var decoder = new ImageDecoder();
        Task<DecodedImage> Decode(string name, DecodeOptions options = default) => Task.Run(() => decoder.DecodeAsync(Path.Combine(folder, name), options, CancellationToken.None));
        // Formats without a Pillow writer: generate deterministic fixtures with the native encoders.
        foreach (var (extension, format) in new[] { ("ras", MagickFormat.Ras), ("wbmp", MagickFormat.Wbmp) })
        {
            using var image = new MagickImage(MagickColors.Red, 64, 48);
            image.Write(Path.Combine(folder, "sample." + extension), format);
        }
        foreach (var (extension, format) in new[] { ("psd", MagickFormat.Psd), ("psb", MagickFormat.Psb) })
        {
            using var collection = new MagickImageCollection();
            collection.Add(new MagickImage(MagickColors.Red, 64, 48)); // Saved composite.
            collection.Add(new MagickImage(MagickColors.Blue, 64, 48)); // Deliberately different layer.
            collection.Write(Path.Combine(folder, "sample." + extension), format);
        }
        var pixels = Enumerable.Repeat(new byte[] { 30,20,230,255 },64*48).SelectMany(x=>x).ToArray();
        var bitmap = BitmapSource.Create(64,48,96,96,PixelFormats.Bgra32,null,pixels,64*4);
        var encoder = new WmpBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(Path.Combine(folder,"sample.hdp"))) encoder.Save(output);
        CreateMetafileFixtures(folder);

        foreach (var extension in new[] { "avif","cur","emf","hdp","icns","pbm","pcx","pgm","ppm","psb","psd","ras","tga","wbmp","wmf" })
            await Test("extended format actual decode: " + extension, async () =>
            {
                var image = await Decode("sample." + extension);
                Check(image.Bitmap.IsFrozen && image.Width > 0 && image.Height > 0, "decoded pixels and metadata");
                Check(ImageCatalog.IsSupported("SAMPLE." + extension.ToUpperInvariant()), "catalog accepts extension");
                var center = Pixel(image,image.Bitmap.PixelWidth/2,image.Bitmap.PixelHeight/2);
                Check(center[3] > 0, "visible pixels");
                if (extension is "psd" or "psb") Check(center[2] > 240 && center[0] < 10 && image.FrameCount == 1, "saved red composite, not blue layer");
                if (extension == "icns") Check(image.Width == 1024 && center[3] is >120 and <140, "largest icon and alpha");
            });
        await Test("classic ICNS planar RLE and transparency mask", async () =>
        {
            var image = await Decode("classic.icns");
            Check(image.Width==16 && Pixel(image,8,8).SequenceEqual(new byte[]{30,20,230,128}),"classic pixels");
        });
        await Test("classic ICNS uncompressed RGB", async () =>
        {
            var image = await Decode("classic-raw.icns");
            Check(image.Width==16 && Pixel(image,8,8).SequenceEqual(new byte[]{30,20,230,255}),"uncompressed pixels");
        });
        await Test("ANI first displayed step, sequence, CUR payload, frame timing and infinite repeat", async () =>
        {
            var image = await Decode("sample.ani");
            Check(image.FrameCount==3 && image.Width==32 && Pixel(image,10,10)[0]>230,"first step points to blue frame");
            using var session = new AniAnimationSession(Path.Combine(folder,"sample.ani"));
            Check(session.RepetitionCount==-1,"infinite repeat");
            foreach(var (step, duration, channel) in new[]{(0,100,0),(1,200,2),(2,50,0),(0,100,0)})
            {
                var frame=session.ReadFrame(step,CancellationToken.None);
                Check(frame.DurationMs==duration,"duration");
                Check(Pixel(new DecodedImage(frame.Bitmap,32,32,0),10,10)[channel]>220,"sequence colors");
            }
        });
        foreach (var file in new[]{"bad-sequence.ani","truncated.ani"})
            await Test("reject malformed ANI: "+file,()=>Expect<InvalidDataException>(()=>Decode(file)));
        await Test("ANI playback, pause and navigation use existing controls", async () =>
        {
            var window=new MainWindow(new ImageDecoder(),null);
            try
            {
                await window.OpenFileAsync(Path.Combine(folder,"sample.ani"));
                await Until(()=>window.AnimationFrameIndex==1);
                window.TogglePlayback(); await Task.Delay(250);
                var paused=window.AnimationFrameIndex; await Task.Delay(250);
                Check(window.AnimationFrameIndex==paused && !window.IsPlaying,"paused");
                await window.OpenFileAsync(Path.Combine(folder,"sample.psd"));
                Check(!window.IsPlaying,"navigation stops ANI");
            }
            finally { window.Close(); }
        });
        await Test("large image above previous 80MP limit uses bounded preview and refinement", async () =>
        {
            var path=Path.Combine(folder,"large.pgm");
            using(var output=File.Create(path))
            {
                var header=System.Text.Encoding.ASCII.GetBytes("P5\n10000 8100\n255\n"); output.Write(header);
                var row=Enumerable.Repeat((byte)128,10000).ToArray();
                for(var y=0;y<8100;y++) output.Write(row);
            }
            var image=await Decode("large.pgm",new DecodeOptions(1024));
            Check(image.Width==10000 && image.Height==8100 && image.Bitmap.PixelWidth==1024,"original metadata and small preview");
            var refined=await Decode("large.pgm");
            Check((long)refined.Bitmap.PixelWidth*refined.Bitmap.PixelHeight<=ImageDecoder.MaxDisplayPixels && refined.IsPreview,"bounded high resolution");
            Check(Pixel(refined,100,100)[2] is >120 and <140,"pixel contents");
        });
        await Test("file above previous 256MiB limit opens", async () =>
        {
            var path=Path.Combine(folder,"large-file.pgm");
            using(var output=File.Create(path))
            {
                output.Write(System.Text.Encoding.ASCII.GetBytes("P5\n1 1\n255\n")); output.WriteByte(128);
                output.SetLength(257L*1024*1024);
            }
            var image=await Decode("large-file.pgm");
            Check(image.Width==1 && image.FileBytes==257L*1024*1024,"file accepted");
        });
        await Test("extended decoder cancellation and output caps", async () =>
        {
            using var canceled=new CancellationTokenSource(); canceled.Cancel();
            await Expect<OperationCanceledException>(()=>Task.Run(()=>decoder.DecodeAsync(Path.Combine(folder,"sample.psd"),canceled.Token)));
            Check(ImageDecoder.DisplayEdge(300000,1,0)==16384,"long edge cap");
            await Expect<NotSupportedException>(()=>Task.Run(()=>ImageDecoder.CheckDimensions(40000,40000)));
        });
    }

    [DllImport("gdi32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr CreateEnhMetaFile(IntPtr hdc,string file,IntPtr rect,string? description);
    [DllImport("gdi32.dll")] private static extern IntPtr CloseEnhMetaFile(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteEnhMetaFile(IntPtr handle);
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr CreateMetaFile(string file);
    [DllImport("gdi32.dll")] private static extern IntPtr CloseMetaFile(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteMetaFile(IntPtr handle);
    [DllImport("gdi32.dll")] private static extern bool Rectangle(IntPtr hdc,int left,int top,int right,int bottom);
    [DllImport("gdi32.dll")] private static extern int SetMapMode(IntPtr hdc,int mode);
    [DllImport("gdi32.dll")] private static extern bool SetWindowExtEx(IntPtr hdc,int x,int y,IntPtr previous);
    private static void CreateMetafileFixtures(string folder)
    {
        var hdc=CreateEnhMetaFile(IntPtr.Zero,Path.Combine(folder,"sample.emf"),IntPtr.Zero,null);
        if(hdc==IntPtr.Zero) throw new Exception("CreateEnhMetaFile failed");
        Rectangle(hdc,0,0,64,48); DeleteEnhMetaFile(CloseEnhMetaFile(hdc));
        var path=Path.Combine(folder,"sample.wmf");
        hdc=CreateMetaFile(path); SetMapMode(hdc,8); SetWindowExtEx(hdc,64,48,IntPtr.Zero);
        Rectangle(hdc,0,0,64,48); DeleteMetaFile(CloseMetaFile(hdc));
        var records=File.ReadAllBytes(path);
        using var output=File.Create(path); using var writer=new BinaryWriter(output);
        using var header=new MemoryStream(); using(var hw=new BinaryWriter(header,System.Text.Encoding.ASCII,true))
        { hw.Write(0x9AC6CDD7u); hw.Write((ushort)0); hw.Write((short)0);hw.Write((short)0);hw.Write((short)64);hw.Write((short)48); hw.Write((ushort)96);hw.Write(0u); }
        var bytes=header.ToArray(); ushort checksum=0;
        for(var i=0;i<bytes.Length;i+=2) checksum^=BitConverter.ToUInt16(bytes,i);
        writer.Write(bytes);writer.Write(checksum);writer.Write(records);
    }
}
