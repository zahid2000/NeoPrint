using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

using NeoPrint.Application.Abstractions;

namespace NeoPrint.Infrastructure.Conversion;

/// <summary>
/// The page images drawn in the last few minutes, in this process's memory.
/// </summary>
/// <remarks>
/// <para>
/// Since the parsers left the API (docs/adr/ADR-0005-isolated-converter.md)
/// every page image costs a process start. A user leafing back and forth
/// through a document asks for the same handful of pages again and again;
/// this answers those from memory and leaves the converter to new work.
/// </para>
/// <para>
/// Memory only, on purpose. A page image is the content of a user's file:
/// it is not written to Redis or to disk, where it would sit unencrypted
/// beside files that are stored encrypted. It is bounded in size and in
/// time, and it is gone when the process stops (docs/DATA_RETENTION.md).
/// </para>
/// </remarks>
public sealed class PagePreviewCache : IPagePreviewCache
{
    private readonly long _capacity;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);

    /// <summary>Most recently used first.</summary>
    private readonly LinkedList<Entry> _order = new();

    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _drawing = new(StringComparer.Ordinal);
    private long _bytes;

    public PagePreviewCache(IOptions<ConversionOptions> options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _capacity = Math.Max(0, options.Value.PreviewCacheMegabytes) * 1024L * 1024L;
        _lifetime = TimeSpan.FromMinutes(Math.Max(0, options.Value.PreviewCacheMinutes));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How many images are held.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>How many bytes they take.</summary>
    public long Bytes
    {
        get
        {
            lock (_sync)
            {
                return _bytes;
            }
        }
    }

    public async Task<byte[]> GetOrAddAsync(
        string key, Func<CancellationToken, Task<byte[]>> render, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(render);

        while (true)
        {
            if (TryGet(key) is { } cached)
            {
                return cached;
            }

            // Requests for the same picture share one drawing. It runs under
            // the token of whoever came first; when that caller gives up, the
            // others draw it themselves instead of failing with them.
            var mine = new Lazy<Task<byte[]>>(() => DrawAsync(key, render, cancellationToken));
            var shared = _drawing.GetOrAdd(key, mine);

            try
            {
                return await shared.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ReferenceEquals(shared, mine) && !cancellationToken.IsCancellationRequested)
            {
                // The first caller left; go round again.
            }
        }
    }

    private async Task<byte[]> DrawAsync(string key, Func<CancellationToken, Task<byte[]>> render, CancellationToken cancellationToken)
    {
        try
        {
            // Never on the caller's stack: the entry must be in the table
            // before its drawing can finish and take it out again.
            await Task.Yield();

            var image = await render(cancellationToken).ConfigureAwait(false);

            Store(key, image);

            return image;
        }
        finally
        {
            _drawing.TryRemove(key, out _);
        }
    }

    private byte[]? TryGet(string key)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                return null;
            }

            var now = _time.GetUtcNow();

            if (now - node.Value.UsedAt > _lifetime)
            {
                Remove(node);
                return null;
            }

            node.Value.UsedAt = now;
            _order.Remove(node);
            _order.AddFirst(node);

            return node.Value.Image;
        }
    }

    private void Store(string key, byte[] image)
    {
        // An image that would not fit even alone is simply not kept.
        if (image.Length == 0 || image.Length > _capacity || _lifetime <= TimeSpan.Zero)
        {
            return;
        }

        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                Remove(existing);
            }

            var now = _time.GetUtcNow();

            // Out of date first, then the least recently used, until it fits.
            while (_order.Last is { } last && (now - last.Value.UsedAt > _lifetime || _bytes + image.Length > _capacity))
            {
                Remove(last);
            }

            _entries[key] = _order.AddFirst(new Entry(key, image) { UsedAt = now });
            _bytes += image.Length;
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _order.Remove(node);
        _entries.Remove(node.Value.Key);
        _bytes -= node.Value.Image.Length;
    }

    private sealed class Entry(string key, byte[] image)
    {
        public string Key { get; } = key;

        public byte[] Image { get; } = image;

        public DateTimeOffset UsedAt { get; set; }
    }
}
