using System.IO;

namespace ImageViewer;

internal readonly record struct DecodeOptions(int MaxEdge = 0, int PageIndex = 0);

// Shared by foreground loads and cancellable neighbor prefetch. No disk image cache.
internal sealed class ImageLoader(IImageDecoder decoder, long budget = 128L * 1024 * 1024)
{
    private sealed record Key(string Path, long Bytes, long Modified, DecodeOptions Options);
    private sealed record Entry(Key Key, DecodedImage Image, long Cost);
    private readonly Dictionary<Key, LinkedListNode<Entry>> _cache = [];
    private readonly LinkedList<Entry> _lru = [];
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _bytes;
    internal long CachedBytes { get { lock (_sync) return _bytes; } }

    public Task<DecodedImage> LoadAsync(string path, DecodeOptions options, CancellationToken token) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        var key = new Key(info.FullName.ToUpperInvariant(), info.Length, info.LastWriteTimeUtc.Ticks, options);
        if (TryGet(key) is { } cached) return cached;
        await _gate.WaitAsync(token);
        try
        {
            if (TryGet(key) is { } queued) return queued;
            var image = await decoder.DecodeAsync(path, options, token);
            token.ThrowIfCancellationRequested();
            // Never cache a file that changed while it was being decoded.
            info.Refresh();
            if (info.Exists && info.Length == key.Bytes && info.LastWriteTimeUtc.Ticks == key.Modified) Add(key, image);
            return image;
        }
        finally { _gate.Release(); }
    }, token);

    private DecodedImage? TryGet(Key key)
    {
        lock (_sync)
        {
            if (!_cache.TryGetValue(key, out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Image;
        }
    }

    private void Add(Key key, DecodedImage image)
    {
        var cost = (long)image.Bitmap.PixelWidth * image.Bitmap.PixelHeight * 4;
        if (cost > budget / 2) return;
        lock (_sync)
        {
            while (_lru.Last is { } last && (_bytes + cost > budget || _cache.Count >= 12))
            {
                _bytes -= last.Value.Cost;
                _cache.Remove(last.Value.Key);
                _lru.RemoveLast();
            }
            var entry = new Entry(key, image, cost);
            _cache.Add(key, _lru.AddFirst(entry));
            _bytes += cost;
        }
    }

    public void Clear() { lock (_sync) { _lru.Clear(); _cache.Clear(); _bytes = 0; } }
}
