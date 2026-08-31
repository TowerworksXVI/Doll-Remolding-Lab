using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Remold.Core.Export;
using Remold.Core.Project;
using Remold.Core.Workbench;

namespace Remold.Core.Migoto;

/// <summary>Persistent completion record for a whole published build. It is deliberately whole-build:
/// a hit serves the folder and distribution zip together, while any input or output mismatch runs the
/// normal compiler and publisher.</summary>
internal static class BuildCompletionCache
{
    private const int Schema = 1;
    private const int SidecarSchema = 1;

    internal sealed record Prepared(string RecordPath, string FinalDir, string ZipPath, bool Zip,
        string InputIdentity, IReadOnlyDictionary<string, string> SourceIdentities);

    private sealed record BundleInput(string Name, string Content);
    private sealed record PublishedFile(string RelativePath, long Length, string Content);
    private sealed record Completion(int Schema, string FinalDir, bool Zip, string InputIdentity,
        IReadOnlyList<BundleInput> Bundles, IReadOnlyList<PublishedFile> Files,
        PublishedFile? ZipFile, IReadOnlyList<string> Warnings, IReadOnlyList<string> Infos,
        IReadOnlyList<string> Diagnostics, IReadOnlyList<string> LogLines);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    internal sealed class BundleObserver
    {
        private readonly BuildEnv _source;
        private readonly HashSet<string> _bundles = new(StringComparer.OrdinalIgnoreCase);

        internal BundleObserver(BuildEnv source)
        {
            _source = source;
            ObservedEnv = source with
            {
                ResolveSubject = ResolveSubject,
                Deobfuscate = Deobfuscate,
                BundleContentHash = BundleContent,
            };
        }

        internal BuildEnv ObservedEnv { get; }
        internal IReadOnlyCollection<string> BundleIds => _bundles;

        private SubjectModel? ResolveSubject(string character, string stem)
        {
            var model = _source.ResolveSubject(character, stem);
            if (model is not null) Observe(model);
            return model;
        }

        private byte[]? Deobfuscate(string bundle)
        {
            Add(bundle);
            return _source.Deobfuscate(bundle);
        }

        private string? BundleContent(string bundle)
        {
            Add(bundle);
            return _source.BundleContentHash?.Invoke(bundle);
        }

        private void Observe(SubjectModel model)
        {
            Add(model.PrimaryBundle);
            foreach (string bundle in model.PrefabBundles ?? Array.Empty<string>()) Add(bundle);
            foreach (string bundle in model.MaterialBundles ?? Array.Empty<string>()) Add(bundle);
            foreach (var part in model.Parts)
            {
                Add(part.MeshBundle);
                Add(part.RendererBundle);
                foreach (var tier in part.SiblingTiers ?? Array.Empty<RecipeTierSlot>())
                {
                    Add(tier.MeshBundle);
                    Add(tier.RendererBundle);
                }
                foreach (var material in part.Materials)
                {
                    Add(material.Bundle);
                    foreach (var map in material.Maps) Add(map.BundleId);
                }
            }
        }

        private void Add(string? bundle)
        {
            if (!string.IsNullOrWhiteSpace(bundle)) _bundles.Add(bundle);
        }
    }

    internal static Prepared? Prepare(AuthoredBuildExecution execution, BuildEnv env,
        string outRoot, bool zip, string completionDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(env.AppVersion)
                || string.IsNullOrWhiteSpace(env.CatalogIdentity)
                || env.BundleContentHash is null)
                return null;

            string packageName = ModNaming.PackageFolderName(execution.Project.Info);
            string finalDir = Path.GetFullPath(Path.Combine(outRoot, packageName));
            string zipPath = Path.GetFullPath(Path.Combine(outRoot, packageName + ".zip"));
            string pathKey = NameKey.Of(finalDir.ToLowerInvariant());
            string recordPath = Path.Combine(completionDir, pathKey + ".json");
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Add(hash, "completion-schema", Schema.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(hash, "compiler", env.CompilerIdentity ?? CompilerIdentity());
            Add(hash, "app-version", env.AppVersion);
            Add(hash, "catalog", env.CatalogIdentity);
            Add(hash, "catalog-version", env.CatalogVersion ?? "");
            Add(hash, "zip", zip ? "1" : "0");
            Add(hash, "project", AuthoredProjectSerializer.Serialize(execution.Project));
            Add(hash, "plan", JsonSerializer.Serialize(execution.Plan, Json));
            Add(hash, "sharing", env.Sharing?.BuildIdentity() ?? "none");

            var referenced = execution.Work.SelectMany(item => item.ReferencedFiles())
                .Concat(execution.StockRamps.Select(ramp => ramp.Ramp))
                .Concat(execution.Project.ProjectAssets.Select(asset => asset.File))
                .Where(file => !string.IsNullOrWhiteSpace(file)).ToList();
            if (execution.Project.Info.Preview is { Length: > 0 } preview) referenced.Add(preview);
            foreach (string relative in referenced.Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
            {
                string path = execution.Project.Resolve(relative);
                string identity = File.Exists(path) ? FileIdentity(path) : "missing";
                Add(hash, "file:" + relative.Replace('\\', '/'), identity);
                if (identity != "missing") sources[Path.GetFullPath(path)] = identity;
            }

            string shaderFile = env.ShaderSlotCatalogFile ?? LabPaths.ShaderSlotCatalogFile;
            Add(hash, "shader-path", Path.GetFullPath(shaderFile));
            Add(hash, "shader-content", File.Exists(shaderFile) ? FileIdentity(shaderFile) : "missing");

            string input = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return new Prepared(recordPath, finalDir, zipPath, zip, input, sources);
        }
        catch
        {
            // A cache key must never become a new refusal path. The compiler below owns all diagnostics.
            return null;
        }
    }

    internal static bool TryServe(Prepared prepared, BuildEnv env, Action<string>? log,
        out ModBuilder.Result result)
    {
        result = null!;
        try
        {
            if (!File.Exists(prepared.RecordPath)) return false;
            var record = JsonSerializer.Deserialize<Completion>(File.ReadAllText(prepared.RecordPath), Json);
            if (record is null || record.Schema != Schema || record.InputIdentity != prepared.InputIdentity
                || record.Zip != prepared.Zip
                || !string.Equals(Path.GetFullPath(record.FinalDir), prepared.FinalDir,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            if (env.BundleContentHash is null || record.Bundles.Any(bundle =>
                    !string.Equals(env.BundleContentHash(bundle.Name), bundle.Content,
                        StringComparison.Ordinal)))
                return false;
            if (!PublishedFolderMatches(prepared.FinalDir, record.Files)) return false;
            if (prepared.Zip)
            {
                if (record.ZipFile is null || !PublishedFileMatches(prepared.ZipPath, record.ZipFile))
                    return false;
            }
            else if (record.ZipFile is not null) return false;

            foreach (string line in record.LogLines) log?.Invoke(line);
            result = new ModBuilder.Result(prepared.FinalDir, prepared.Zip ? prepared.ZipPath : null,
                record.Warnings, record.Infos, record.Diagnostics);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static void TryPublish(Prepared prepared, BuildEnv env, IEnumerable<string> bundleIds,
        ModBuilder.Result result, IReadOnlyList<string> logLines)
    {
        try
        {
            if (env.BundleContentHash is null) return;
            var bundles = new List<BundleInput>();
            foreach (string bundle in bundleIds.Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                string? content = env.BundleContentHash(bundle);
                if (content is null) return;
                bundles.Add(new BundleInput(bundle, content));
            }
            var files = Directory.EnumerateFiles(prepared.FinalDir, "*", SearchOption.AllDirectories)
                .OrderBy(file => file, StringComparer.Ordinal)
                .Select(file => Published(file, Path.GetRelativePath(prepared.FinalDir, file)
                    .Replace(Path.DirectorySeparatorChar, '/'))).ToList();
            PublishedFile? zipFile = prepared.Zip ? Published(prepared.ZipPath,
                Path.GetFileName(prepared.ZipPath)) : null;
            var record = new Completion(Schema, prepared.FinalDir, prepared.Zip, prepared.InputIdentity,
                bundles, files, zipFile, result.Warnings.ToArray(), result.Infos.ToArray(),
                result.Diagnostics.ToArray(), logLines.ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(prepared.RecordPath)!);
            string temp = prepared.RecordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(record, Json), new UTF8Encoding(false));
                File.Move(temp, prepared.RecordPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) try { File.Delete(temp); } catch { }
            }
        }
        catch
        {
            // The published build is authoritative. Losing its optional reuse record changes no result.
        }
    }

    private static bool PublishedFolderMatches(string directory, IReadOnlyList<PublishedFile> expected)
    {
        if (!Directory.Exists(directory)) return false;
        var actual = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(file => file, StringComparer.Ordinal).ToList();
        if (actual.Count != expected.Count) return false;
        for (int i = 0; i < actual.Count; i++)
        {
            string relative = Path.GetRelativePath(directory, actual[i])
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!string.Equals(relative, expected[i].RelativePath, StringComparison.Ordinal)
                || !PublishedFileMatches(actual[i], expected[i]))
                return false;
        }
        return true;
    }

    private static bool PublishedFileMatches(string file, PublishedFile expected)
    {
        if (!File.Exists(file)) return false;
        var info = new FileInfo(file);
        return info.Length == expected.Length
            && string.Equals(FileIdentity(file), expected.Content, StringComparison.Ordinal);
    }

    private static PublishedFile Published(string file, string relative) =>
        new(relative, new FileInfo(file).Length, FileIdentity(file));

    private static string FileIdentity(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string CompilerIdentity() => string.Join("|",
        CoreBuildIdentity.ShortHash,
        AuthoredProject.CurrentSchema,
        RepairData.Schema,
        SidecarSchema);

    private static void Add(IncrementalHash hash, string name, string value)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] valueBytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(nameBytes.Length));
        hash.AppendData(nameBytes);
        hash.AppendData(BitConverter.GetBytes(valueBytes.Length));
        hash.AppendData(valueBytes);
    }
}
