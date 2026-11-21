using System.IO;
using System.Runtime.InteropServices;

namespace ImageViewer;

internal sealed record ImageCatalog(string[] Files, int SelectedIndex)
{
    public static readonly string[] Extensions = [".ani", ".avif", ".bmp", ".cur", ".emf", ".gif", ".hdp", ".icns", ".ico", ".jfif", ".jpe", ".jpeg", ".jpg", ".pbm", ".pcx", ".pgm", ".png", ".ppm", ".psb", ".psd", ".ras", ".tga", ".tif", ".tiff", ".wbmp", ".webp", ".wmf"];
    private static readonly HashSet<string> SupportedExtensions = new(Extensions, StringComparer.OrdinalIgnoreCase);
    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    public static ImageCatalog Open(string filePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!IsSupported(fullPath))
            throw new NotSupportedException("지원하지 않는 파일 형식입니다. 파일 열기 창의 지원 이미지 목록을 확인해 주세요.");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("파일을 찾을 수 없습니다. 이동 또는 삭제되었는지 확인해 주세요.", fullPath);
        var files = new List<(string Path, string Name)>();
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(fullPath)!, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSupported(file)) files.Add((file, Path.GetFileName(file)));
        }
        var comparisons = 0;
        try
        {
            files.Sort((left, right) =>
            {
                if ((++comparisons & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                return NaturalFileComparer.CompareNames(left.Name, right.Name);
            });
        }
        catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        cancellationToken.ThrowIfCancellationRequested();
        var selected = files.FindIndex(file => string.Equals(file.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (selected < 0) throw new FileNotFoundException("폴더를 읽는 동안 파일이 이동 또는 삭제되었습니다.", fullPath);
        return new ImageCatalog(files.Select(file => file.Path).ToArray(), selected);
    }

    public static int Wrap(int index, int delta, int count) => count == 0 ? -1 : (int)(((long)index + delta % count + count) % count);
}

internal sealed class NaturalFileComparer : IComparer<string>
{
    public static readonly NaturalFileComparer Instance = new();
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string left, string right);

    public int Compare(string? left, string? right)
    {
        return CompareNames(Path.GetFileName(left) ?? "", Path.GetFileName(right) ?? "");
    }
    internal static int CompareNames(string left, string right)
    {
        var result = StrCmpLogicalW(left, right);
        return result != 0 ? result : StringComparer.Ordinal.Compare(left, right);
    }
}
