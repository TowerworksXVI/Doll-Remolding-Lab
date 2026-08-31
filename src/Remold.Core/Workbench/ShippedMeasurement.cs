using System;
using System.IO;
using System.Text.Json;

namespace Remold.Core.Workbench;

/// <summary>
/// The release-time check that the shipped measurement pair is readable by the code shipping beside it.
///
/// <para><b>Why a check at all.</b> Both files are refused at LOAD when their schema is not the current one
/// — the safe degradation, and a silent one: the app simply measures the whole population on first launch,
/// which is exactly the cost the seed exists to avoid. Nothing in a build, a test run or a publish notices,
/// because a refused seed is indistinguishable from a fresh install. So the one place that CAN notice is
/// the pack, and it refuses rather than shipping a seed no install will read.</para>
///
/// <para>The expected numbers are read off the types that write the files
/// (<see cref="SharingIndex.SchemaVersion"/>, <see cref="AssetHashMemo.SchemaVersion"/>), never restated
/// here — a guard carrying its own copy of the number it guards is a guard that goes stale on the first
/// bump.</para>
/// </summary>
public static class ShippedMeasurement
{
    /// <summary>What a modder is told to do about it, once per complaint.</summary>
    private const string Remedy = "re-mint the seed pair from one full measure "
        + "(LabPaths.SharingSeedFile states the procedure) and re-publish";

    /// <summary>Why the pair under <paramref name="dir"/> may not ship, or null when both files are
    /// current. <paramref name="dir"/> is a publish folder — the layout the build lays the pair down in,
    /// which is also the layout the app reads them back from.</summary>
    public static string? Refusal(string dir) =>
        Complaint(dir, LabPaths.SharingSeedRelativePath,
            ("SchemaVersion", SharingIndex.SchemaVersion))
        ?? SharingRowsComplaint(dir)
        ?? Complaint(dir, LabPaths.AssetHashSeedRelativePath,
            ("SchemaVersion", AssetHashMemo.SchemaVersion),
            ("SharingSchemaVersion", SharingIndex.SchemaVersion))
        ?? MemoEntriesComplaint(dir);

    /// <summary>The current writer always emits a nonempty outfit set and every field of every row. An
    /// address-resolution record may itself be empty (a row with no catalog-resolved part address), while
    /// the read record may not: an empty R is deliberately never reusable.</summary>
    private static string? SharingRowsComplaint(string dir)
    {
        string relative = LabPaths.SharingSeedRelativePath;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, relative)));
        if (!doc.RootElement.TryGetProperty("Outfits", out var outfits)
            || outfits.ValueKind != JsonValueKind.Array || outfits.GetArrayLength() == 0)
            return $"{relative} carries no outfit measurements — {Remedy}.";

        int row = 0;
        foreach (var outfit in outfits.EnumerateArray())
        {
            if (outfit.ValueKind != JsonValueKind.Object)
                return $"{relative} outfit row {row} is not a measurement row — {Remedy}.";
            foreach (var field in new[] { "K", "F" })
                if (!outfit.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
                    return $"{relative} outfit row {row} is missing {field} — {Remedy}.";
            foreach (var field in new[] { "M", "T", "W" })
                if (!outfit.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Array)
                    return $"{relative} outfit row {row} is missing {field} — {Remedy}.";
            if (!outfit.TryGetProperty("R", out var reads) || reads.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(reads.GetString()))
                return $"{relative} outfit row {row} carries no reusable read record R — {Remedy}.";
            // Save writes A for every row, including "" when no part address needed catalog resolution.
            if (!outfit.TryGetProperty("A", out var addresses) || addresses.ValueKind != JsonValueKind.String)
                return $"{relative} outfit row {row} is missing address record A — {Remedy}.";
            row++;
        }
        return null;
    }

    /// <summary>A schema-current but empty memo defeats the pair's purpose just as surely as no memo: every
    /// asset behind a row invalidated by a patch would be measured from its bundle again.</summary>
    private static string? MemoEntriesComplaint(string dir)
    {
        string relative = LabPaths.AssetHashSeedRelativePath;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, relative)));
        if (!doc.RootElement.TryGetProperty("Entries", out var entries)
            || entries.ValueKind != JsonValueKind.Object || !entries.EnumerateObject().MoveNext())
            return $"{relative} carries no measured asset hashes — {Remedy}.";
        return null;
    }

    /// <summary>The complaint about one file: absent, unreadable, or stating a schema this build does not
    /// read. Every number the file has to state is checked, so a pair that is half current is still
    /// refused.</summary>
    private static string? Complaint(string dir, string relative,
        params (string Field, int Expected)[] required)
    {
        string path = Path.Combine(dir, relative);
        if (!File.Exists(path)) return $"{relative} is missing — re-publish the app.";
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (Exception e) { return $"{relative} is not readable JSON ({e.Message}) — {Remedy}."; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return $"{relative} is not a measurement file — {Remedy}.";
            foreach (var (field, expected) in required)
            {
                if (!doc.RootElement.TryGetProperty(field, out var stated)
                    || stated.ValueKind != JsonValueKind.Number || !stated.TryGetInt32(out int found))
                    return $"{relative} states no {field}, so it predates this build's format "
                        + $"(which reads {expected}) — {Remedy}.";
                if (found != expected)
                    return $"{relative} states {field} {found} and this build reads {expected}, "
                        + $"so the app would refuse it at load — {Remedy}.";
            }
        }
        return null;
    }
}
