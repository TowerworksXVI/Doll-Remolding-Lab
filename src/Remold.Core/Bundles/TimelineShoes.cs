using System;
using System.Collections.Generic;
using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Remold.Core.Bundles;

/// <summary>One timeline's node overrides: the names it shows and the names it hides while the clip plays.
/// Both lists demote alike — a node either list can flip is one the scene moves independently of the parts
/// around it.</summary>
public sealed record TimelineShoe(IReadOnlyList<string> ShowNodes, IReadOnlyList<string> HideNodes);

/// <summary>
/// Reads the node-override component off the dorm and lobby timeline prefabs a doll plays. The component
/// also forces the whole scene context for the clip's duration, which needs no handling: a whole-context
/// flip moves every part together and leaves the recovery pairs it would break intact. Only the two node
/// LISTS matter, because those flip individual nodes and produce the mixed states.
///
/// <para>The component is found by FIELD SHAPE (<c>ShowNodesList</c> alongside <c>ShowShoe</c>), never by
/// script name — script names aren't shipped. Its lists are plain STRINGS, unlike the character prefab's
/// Transform references.</para>
/// </summary>
public static class TimelineShoes
{
    /// <summary>Read the overrides carried by every address that resolves. An address with no catalog entry
    /// is skipped in silence: the templates describe the whole roster's clip vocabulary, so most names never
    /// belong to any one character. A bundle that resolves but can't be read is skipped too — an unread
    /// timeline demotes nothing, which is the same answer as a character with no timelines at all.
    ///
    /// <para><paramref name="unreadableBundles"/> is how many bundles the catalog DID resolve and this
    /// could not open, which is what separates "this character plays no timeline that overrides a node"
    /// from "the install would not give up its timelines" (the game holding the files open being the
    /// common cause). Both answer with the same empty list, and only the caller can say which one it is
    /// looking at, so the count rides out rather than being decided here.</para></summary>
    public static IReadOnlyList<TimelineShoe> Read(CatalogIndex catalog, Func<string, byte[]?> tryDeobfuscate,
        IEnumerable<string> addresses, out int unreadableBundles)
    {
        var shoes = new List<TimelineShoe>();
        var readBundles = new HashSet<string>(StringComparer.Ordinal);
        unreadableBundles = 0;
        foreach (var address in addresses)
        {
            if (catalog.ResolveAddress(address) is not { } bundle) continue;
            // several clips of one character commonly share a bundle; each is read once
            if (!readBundles.Add(bundle)) continue;
            byte[]? bytes;
            try { bytes = tryDeobfuscate(bundle); }
            catch { unreadableBundles++; continue; }
            if (bytes is null) { unreadableBundles++; continue; }
            // Bytes in hand and no override component found is a real, silent answer: most clips carry
            // none. Only a bundle that would not PARSE joins the unreadable count.
            try { ReadBundle(bytes, shoes); }
            catch { unreadableBundles++; }
        }
        return shoes;
    }

    private static void ReadBundle(byte[] deobfuscatedBundle, List<TimelineShoe> into)
    {
        var am = new AssetsManager();
        var bun = am.LoadBundleFile(new MemoryStream(deobfuscatedBundle), "timeline.bundle");
        var inst = am.LoadAssetsFileFromBundle(bun, 0);
        foreach (var info in inst.file.AssetInfos)
        {
            if (info.TypeId != BundleReader.ClassMonoBehaviour) continue;
            AssetTypeValueField mb;
            try { mb = am.GetBaseField(inst, info); } catch { continue; }
            // the field pair that identifies the component; either list may legitimately be empty
            if (mb["ShowNodesList"].IsDummy || mb["ShowShoe"].IsDummy) continue;
            var show = Strings(mb["ShowNodesList"]);
            var hide = Strings(mb["HideNodesList"]);
            if (show.Count > 0 || hide.Count > 0) into.Add(new TimelineShoe(show, hide));
        }
    }

    private static IReadOnlyList<string> Strings(AssetTypeValueField list)
    {
        var values = new List<string>();
        if (list.IsDummy) return values;
        foreach (var e in BundleReader.UnwrapArray(list))
        {
            var s = e.AsString;
            if (!string.IsNullOrEmpty(s)) values.Add(s);
        }
        return values;
    }
}
