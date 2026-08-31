using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.IO;
using Avalonia.Media.Imaging;
using Remold.App.Textures;

namespace Remold.App.ViewModels.EditPage;

/// <summary>A decoded session ramp candidate. Choice is the exact answer the picker applies; labels are the
/// material places (or project-asset name) from which the same candidate was reached.</summary>
internal sealed record SessionRampReadCandidate(RampChoice Choice, IReadOnlyList<string> Labels,
    bool IsOwn, bool IsBound, RampImage.Read? Image, byte[]? PreviewPng = null);

/// <summary>Turn session-owned ramp candidates into the picker's model without consulting a legacy project.
/// Non-own rows fold by stored fp16 bytes; the semantic keep-own row remains distinct even when another place
/// happens to carry the same curve.</summary>
internal static class SessionRampRows
{
    internal static RampPickLoad Fold(IReadOnlyList<SessionRampReadCandidate> candidates)
    {
        var groups = new List<(SessionRampReadCandidate First, RampImage.Read Read,
            List<string> Labels, bool Bound)>();
        var byContent = new Dictionary<string, int>(StringComparer.Ordinal);
        SessionRampReadCandidate? unreadOwn = null;
        SessionRampReadCandidate? unreadBound = null;

        foreach (var candidate in candidates)
        {
            if (candidate.Image is not { } image)
            {
                if (candidate.IsOwn) unreadOwn = candidate;
                else if (candidate.IsBound) unreadBound = candidate;
                continue;
            }
            string key = candidate.IsOwn ? "own"
                : Convert.ToHexString(SHA256.HashData(image.Fp16));
            if (byContent.TryGetValue(key, out int at))
            {
                foreach (string label in candidate.Labels)
                    if (!groups[at].Labels.Contains(label, StringComparer.Ordinal))
                        groups[at].Labels.Add(label);
                if (candidate.IsBound && !groups[at].Bound)
                    groups[at] = groups[at] with { Bound = true };
                continue;
            }
            byContent[key] = groups.Count;
            groups.Add((candidate, image, new List<string>(candidate.Labels), candidate.IsBound));
        }

        var rows = new List<RampPickRowVm>(groups.Count + (unreadOwn is null ? 0 : 1));
        if (unreadOwn is not null) rows.Add(Own(unreadOwn, null));
        if (unreadBound is not null) rows.Add(Pending(unreadBound));
        foreach (var group in groups)
        {
            var row = group.First.IsOwn
                ? Own(group.First with { IsBound = group.Bound }, group.Read)
                : Place(group.First.Choice, group.Labels, group.Bound, group.Read,
                    group.First.PreviewPng);
            rows.Add(row);
        }
        rows.Sort((left, right) => right.IsOwn.CompareTo(left.IsOwn));
        return new RampPickLoad(rows);
    }

    private static RampPickRowVm Own(SessionRampReadCandidate candidate, RampImage.Read? image)
    {
        var preview = image is { } read ? Preview(candidate, read) : null;
        return new RampPickRowVm
        {
            Choice = RampChoice.KeepOwn,
            Title = candidate.Labels.FirstOrDefault() ?? "Default",
            SourcesTip = candidate.Labels.Count > 1 ? string.Join('\n', candidate.Labels.Skip(1)) : null,
            Dimensions = image is { } shown ? $"{shown.Width}×{shown.Height}" : "",
            IsOwn = true,
            IsBound = candidate.IsBound,
        }.Settled(preview);
    }

    private static RampPickRowVm Pending(SessionRampReadCandidate candidate) => new()
    {
        Choice = candidate.Choice,
        Title = candidate.Labels.FirstOrDefault() ?? candidate.Choice.Texture ?? "Toon ramp",
        IsBound = true,
    };

    private static RampPickRowVm Place(RampChoice choice, IReadOnlyList<string> labels, bool bound,
        RampImage.Read image, byte[]? previewPng) => new RampPickRowVm
        {
            Choice = choice,
            Title = labels.FirstOrDefault() ?? (choice.File is { } file
                ? System.IO.Path.GetFileName(file) : choice.Texture ?? "Toon ramp"),
            Source = labels.Count > 1 ? $"and {labels.Count - 1} more" : "",
            SourcesTip = labels.Count > 1 ? string.Join('\n', labels) : null,
            Dimensions = $"{image.Width}×{image.Height}",
            IsBound = bound,
        }.Settled(PreviewFor(choice, image, previewPng));

    private static Bitmap? Preview(SessionRampReadCandidate candidate, RampImage.Read image) =>
        PreviewFor(candidate.Choice, image, candidate.PreviewPng);

    private static Bitmap? PreviewFor(RampChoice choice, RampImage.Read image, byte[]? cached)
    {
        if (cached is not null)
        {
            try
            {
                using var stream = new MemoryStream(cached, writable: false);
                return Bitmap.DecodeToWidth(stream, image.Width);
            }
            catch { }
        }
        return RampImage.TryPreview(image.Width, image.Height, image.Fp16);
    }

    internal static byte[]? RenderPreview(RampImage.Read image)
    {
        try
        {
            using var preview = RampImage.TryPreview(image.Width, image.Height, image.Fp16);
            if (preview is null) return null;
            using var stream = new MemoryStream();
            preview.Save(stream);
            return stream.ToArray();
        }
        catch { return null; }
    }
}
