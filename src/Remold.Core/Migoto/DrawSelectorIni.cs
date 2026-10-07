using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Remold.Core.Project;

namespace Remold.Core.Migoto;

/// <summary>Lowers draw-selector keys at the complete INI boundary. A section keyed on a compound selector
/// keeps <c>hash = ib</c> and wraps its commands in a predicate over the constrained vertex slots alone —
/// the index buffer is the section's own match; a bare key is left as it stands. Section matching metadata
/// stays outside the predicate; every command, including captures and presence observations, stays inside.
/// Slot expressions query bound resources without executing their override command lists.
///
/// <para>The tag checks here are a last line behind the plan-time refusal
/// (<see cref="MigotoEmitter.RefuseTagCollisions"/>), which names the rows to leave out; reaching one of
/// these is a build defect, not an authoring outcome.</para></summary>
internal static class DrawSelectorIni
{
    public static string Lower(string ini)
    {
        var headers = Regex.Matches(ini, @"(?m)^\[[^\]\r\n]+\]\r?$");
        var sections = new List<(string Header, string Body)>();
        for (int i = 0; i < headers.Count; i++)
        {
            int start = headers[i].Index + headers[i].Length;
            int end = i + 1 < headers.Count ? headers[i + 1].Index : ini.Length;
            sections.Add((headers[i].Value.TrimEnd('\r'), ini[start..end]));
        }
        string? Value(string body, string key) => Regex.Match(body,
            @"(?m)^" + Regex.Escape(key) + @"\s*=\s*([^\r\n]+)").Groups[1] is { Success: true } match
                ? match.Value.Trim() : null;
        var required = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var section in sections.Where(s => s.Header.StartsWith("[TextureOverride_", StringComparison.Ordinal)))
            if (Value(section.Body, "hash") is { } key && DrawSelector.Parse(key) is { IsCompound: true } selector)
                foreach (var binding in selector.SlotBindings()) required.Add(binding.Hash);
        if (required.Count == 0) return ini;

        var tagOwners = new Dictionary<int, HashSet<string>>();
        var existing = new Dictionary<string, List<(int Priority, int Tag)>>(StringComparer.Ordinal);
        foreach (var section in sections.Where(s => s.Header.StartsWith("[TextureOverride_", StringComparison.Ordinal)))
        {
            if (Value(section.Body, "hash") is not { Length: 8 } hash
                || !int.TryParse(Value(section.Body, "filter_index"), CultureInfo.InvariantCulture, out int tag)) continue;
            int.TryParse(Value(section.Body, "match_priority"), CultureInfo.InvariantCulture, out int priority);
            if (!existing.TryGetValue(hash, out var tags)) existing[hash] = tags = new();
            tags.Add((priority, tag));
            if (!tagOwners.TryGetValue(tag, out var owners)) tagOwners[tag] = owners = new(StringComparer.Ordinal);
            owners.Add(hash);
        }
        var addTags = new List<(string Hash, int Tag)>();
        foreach (string hash in required)
        {
            int tag = MigotoEmitter.RetexTag(hash);
            if (tagOwners.TryGetValue(tag, out var owners) && owners.Any(owner => owner != hash))
                throw new InvalidOperationException($"draw tag {tag} is derived by vertex buffer {hash} and by "
                    + $"{string.Join(", ", owners.Where(owner => owner != hash))}; the plan-time tag check should have refused this build");
            tagOwners[tag] = new(StringComparer.Ordinal) { hash };
            if (existing.TryGetValue(hash, out var tags))
            {
                int top = tags.Max(t => t.Priority);
                if (tags.Where(t => t.Priority == top).Any(t => t.Tag != tag))
                    throw new InvalidOperationException($"vertex buffer {hash} already carries slot tag "
                        + $"{tags.First(t => t.Priority == top).Tag}, not its draw tag {tag}; the plan-time tag check should have refused this build");
            }
            else addTags.Add((hash, tag));
        }

        var output = new StringBuilder(ini[..headers[0].Index]);
        foreach (var section in sections)
        {
            output.Append(section.Header);
            string? key = section.Header.StartsWith("[TextureOverride_", StringComparison.Ordinal)
                ? Value(section.Body, "hash") : null;
            if (key is null || DrawSelector.Parse(key) is not { IsCompound: true } selector)
            {
                output.Append(section.Body);
                continue;
            }
            var commands = new List<string>();
            output.Append('\n');
            foreach (string raw in section.Body.Trim('\r', '\n').Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith("hash = ", StringComparison.Ordinal))
                    output.Append("hash = ").Append(selector.Hash).Append('\n');
                else if (line.StartsWith("match_", StringComparison.Ordinal)
                    || line.StartsWith("filter_index", StringComparison.Ordinal)) output.Append(line).Append('\n');
                else commands.Add(line);
            }
            output.Append("if ").Append(string.Join(" && ", selector.SlotBindings()
                .Select(binding => $"{binding.Slot} == {MigotoEmitter.RetexTag(binding.Hash)}"))).Append('\n');
            foreach (string line in commands) output.Append(line).Append('\n');
            output.Append("endif\n\n");
        }
        foreach (var (hash, tag) in addTags)
            output.Append($"[TextureOverride_DrawTag_{hash}]\nhash = {hash}\nfilter_index = {tag}\nmatch_priority = 100\n\n");
        return output.ToString();
    }
}
