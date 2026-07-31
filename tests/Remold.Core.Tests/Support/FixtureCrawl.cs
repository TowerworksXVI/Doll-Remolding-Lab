using System;
using System.Collections.Generic;
using System.IO;
using Remold.Core.Bundles;

namespace Remold.Core.Tests.Support;

/// <summary>
/// A logical-id → plain-bytes deobfuscate func over a fixture dir: walk every segment of every
/// <c>*.bundle</c> and key each by its self-declared logical name. An unknown logical id resolves to
/// null — the "referenced but absent" contract those tests rely on.
/// </summary>
internal static class FixtureCrawl
{
    /// <summary>A deobfuscate func over every readable segment under <paramref name="bundleDir"/>
    /// (eager: the fixture files are read once, up front).</summary>
    public static Func<string, byte[]?> DeobfuscateOver(string bundleDir)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var reader = new BundleReader();
        foreach (var file in Directory.GetFiles(bundleDir, "*.bundle"))
        {
            byte[] raw;
            BundleSegments.WalkResult walk;
            try { raw = File.ReadAllBytes(file); walk = BundleSegments.Walk(raw); }
            catch { continue; }   // non-bundle fixture noise — same skip the crawls make
            foreach (var seg in walk.Segments)
            {
                try
                {
                    var dec = BundleSegments.ExtractPlain(raw, seg);
                    map[reader.GetBundleName(dec)] = dec;
                }
                catch { /* unreadable segment — skip, like the crawls */ }
            }
        }
        return l => map.TryGetValue(l, out var b) ? b : null;
    }
}
