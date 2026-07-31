using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Remold.Core;

/// <summary>
/// Records that the modder accepted a first-run pop-up, stamped with a hash of the EXACT text they saw,
/// so any edit to the copy re-prompts with no manual version bump. Durable — stored beside the
/// settings (<see cref="LabPaths.FirstRunAcceptanceFile"/>).
/// </summary>
public static class FirstRunGate
{
    private sealed record Record(string AcceptedStamp);

    /// <summary>SHA-256 hex of the text's UTF-8 bytes. Whitespace-sensitive on purpose — any edit is a
    /// change, and re-prompting is the safe direction.</summary>
    public static string Stamp(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>True only when the stored acceptance matches the CURRENT text's stamp. A missing, corrupt, or
    /// stale-stamp record reads as not-accepted, so the caller re-prompts (the safe direction).</summary>
    public static bool IsAccepted(string text, string? path = null)
    {
        path ??= LabPaths.FirstRunAcceptanceFile;
        try
        {
            if (!File.Exists(path)) return false;
            var rec = JsonSerializer.Deserialize<Record>(File.ReadAllText(path));
            return rec is not null && rec.AcceptedStamp == Stamp(text);
        }
        catch { return false; }
    }

    /// <summary>Persist acceptance of the given text. Best effort — a failed write only means the next
    /// launch re-prompts.</summary>
    public static void RecordAccepted(string text, string? path = null)
    {
        path ??= LabPaths.FirstRunAcceptanceFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new Record(Stamp(text))));
        }
        catch { /* best effort — a failed write just re-prompts next launch */ }
    }
}
