using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Remold.Core.Migoto;

/// <summary>
/// The persistent side of the tier material map: a finished map, filed under a caller's own key and read
/// back instead of measured again.
///
/// <para>Every entry is filed under the key the caller states the rules and inputs in, so a change to
/// either retires the entries it would have changed rather than serving last week's reading. A miss —
/// absent, unreadable, truncated, or written under a different key that hashed the same — costs the
/// measurement and never a wrong answer.</para>
///
/// <para>A BUILD neither reads nor writes here: its routing is decided by material identity alone, which
/// it measures on every build.</para>
/// </summary>
internal static class TierMapCache
{
    /// <summary>Payload layout tag; a change to the field list changes it, so old entries read as a
    /// miss.</summary>
    private const string CacheFormat = "remold-tiermap-1";

    /// <summary>The entries filed under <paramref name="key"/>, or null on any miss.</summary>
    public static IReadOnlyList<TierMapEntry>? Read(string dir, string key)
    {
        try
        {
            var lines = File.ReadAllLines(PathFor(dir, key));
            if (lines.Length < 2 || lines[0] != CacheFormat || lines[1] != key) return null;
            var entries = new List<TierMapEntry>(lines.Length - 2);
            for (int i = 2; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                var parts = lines[i].Split(' ');
                if (parts.Length != 3
                    || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int position)
                    || !Enum.TryParse(parts[2], ignoreCase: false, out TierMapRule rule)) return null;
                int? carrier = null;
                if (parts[1] != "none")
                {
                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int q)) return null;
                    carrier = q;
                }
                entries.Add(new TierMapEntry(position, carrier, rule));
            }
            return entries;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>File the entries under <paramref name="key"/>, atomically (unique temp + move) so a reader
    /// on another build never sees a partial payload. A write that fails leaves no entry and is not
    /// reported: the map itself is in hand either way.</summary>
    public static void Write(string dir, string key, IReadOnlyList<TierMapEntry> entries)
    {
        string path = PathFor(dir, key);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(dir);
            CacheTemps.SweepOnce(dir);
            var text = new StringBuilder().AppendLine(CacheFormat).AppendLine(key);
            foreach (var e in entries)
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{e.Position} {(e.Carrier is { } q ? q.ToString(CultureInfo.InvariantCulture) : "none")} {e.Rule}"));
            File.WriteAllText(tmp, text.ToString(), Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Filing is best-effort in every one of its failure modes — a full disk, a locked entry, a
            // cache root that cannot be created: the measured map is in hand either way.
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ } }
        }
    }

    /// <summary>Where the map with this identity lives. The name is a hash of the identity, which the
    /// payload repeats so a hash collision reads as a miss rather than as another part's map.</summary>
    private static string PathFor(string dir, string key) =>
        Path.Combine(dir,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".map");
}
