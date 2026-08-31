using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Remold.Core.Bundles;

namespace Remold.Core.Workbench;

/// <summary>Bounded, single-flight cache used only while the launch roster fill is running. It coalesces
/// physical bundle deobfuscation and the expensive assembly-prefab parse across outfits that share a
/// bundle. Retained raw bytes never exceed <see cref="ByteBudget"/>; parse entries are evicted with their
/// owning raw bytes.</summary>
public sealed class RosterFillCache
{
    public const long DefaultByteBudget = 256L * 1024 * 1024;

    private sealed record ByteEntry(string Bundle, byte[] Bytes);
    internal readonly record struct PrefabResult(CharacterPrefab? Prefab, bool Declined);

    private readonly Func<string, byte[]?> _loader;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<ByteEntry>> _bytes =
        new(StringComparer.Ordinal);
    private readonly LinkedList<ByteEntry> _lru = new();
    private readonly Dictionary<(string Bundle, string Root), PrefabResult> _parsed = new();
    private readonly ConcurrentDictionary<string, Lazy<byte[]?>> _readFlights =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Bundle, string Root), Lazy<PrefabResult>> _parseFlights = new();
    private long _cachedBytes;

    public RosterFillCache(Func<string, byte[]?> loader, long byteBudget = DefaultByteBudget)
    {
        ArgumentNullException.ThrowIfNull(loader);
        if (byteBudget < 0) throw new ArgumentOutOfRangeException(nameof(byteBudget));
        _loader = loader;
        ByteBudget = byteBudget;
    }

    public long ByteBudget { get; }
    internal long CachedBytes { get { lock (_gate) return _cachedBytes; } }

    public byte[]? Read(string bundle)
    {
        lock (_gate)
        {
            if (_bytes.TryGetValue(bundle, out var hit))
            {
                _lru.Remove(hit);
                _lru.AddLast(hit);
                return hit.Value.Bytes;
            }
        }

        var flight = _readFlights.GetOrAdd(bundle, key => new Lazy<byte[]?>(
            () => _loader(key), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            byte[]? loaded = flight.Value;
            if (loaded is not null) Retain(bundle, loaded);
            return loaded;
        }
        finally
        {
            _readFlights.TryRemove(new KeyValuePair<string, Lazy<byte[]?>>(bundle, flight));
        }
    }

    internal CharacterPrefab? Parse(string bundle, byte[] bytes, string? rootName, out bool declined)
    {
        string root = rootName ?? "";
        var key = (bundle, root);
        lock (_gate)
            if (_parsed.TryGetValue(key, out var hit))
            {
                declined = hit.Declined;
                return hit.Prefab;
            }

        var flight = _parseFlights.GetOrAdd(key, _ => new Lazy<PrefabResult>(() =>
        {
            var prefab = PrefabReader.Read(bytes, rootName, out bool wasDeclined);
            return new PrefabResult(prefab, wasDeclined);
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var parsed = flight.Value;
            lock (_gate)
                if (_bytes.TryGetValue(bundle, out var retained)
                    && ReferenceEquals(retained.Value.Bytes, bytes))
                    _parsed[key] = parsed;
            declined = parsed.Declined;
            return parsed.Prefab;
        }
        finally
        {
            _parseFlights.TryRemove(new KeyValuePair<(string Bundle, string Root), Lazy<PrefabResult>>(key, flight));
        }
    }

    private void Retain(string bundle, byte[] bytes)
    {
        if (bytes.LongLength > ByteBudget) return;
        lock (_gate)
        {
            if (_bytes.TryGetValue(bundle, out var have))
            {
                _lru.Remove(have);
                _lru.AddLast(have);
                return;
            }
            while (_cachedBytes + bytes.LongLength > ByteBudget && _lru.First is { } oldest)
            {
                _lru.RemoveFirst();
                _bytes.Remove(oldest.Value.Bundle);
                _cachedBytes -= oldest.Value.Bytes.LongLength;
                foreach (var parsedKey in new List<(string Bundle, string Root)>(_parsed.Keys))
                    if (string.Equals(parsedKey.Bundle, oldest.Value.Bundle, StringComparison.Ordinal))
                        _parsed.Remove(parsedKey);
            }
            var node = _lru.AddLast(new ByteEntry(bundle, bytes));
            _bytes[bundle] = node;
            _cachedBytes += bytes.LongLength;
        }
    }
}
