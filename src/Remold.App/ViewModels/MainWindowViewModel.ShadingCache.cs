using System.Runtime.CompilerServices;
using Remold.App.ViewModels.EditPage;
using Remold.Core;
using Remold.Core.Bundles;
using Remold.Core.Project;
using Remold.Core.Workbench;

namespace Remold.App.ViewModels;

public partial class MainWindowViewModel
{
    private readonly ConditionalWeakTable<GameVfs, MaterialShadingCache> _materialShading = new();

    internal Func<GameVfs, GameAssetRef, EditShadingRead>? MaterialShadingReadForTest { get; set; }

    /// <summary>The same install boundary as material evidence. Opening another mod or adding the same
    /// outfit again reuses its answers; a rescan replaces the install and every material answer with it.</summary>
    internal MaterialShadingCache MaterialShadingFor(GameVfs vfs)
    {
        return _materialShading.GetValue(vfs, install =>
        {
            var evidence = MaterialEvidenceFor(install);
            return new MaterialShadingCache(material =>
                MaterialShadingReadForTest is { } read ? read(install, material)
                    : ReadMaterialShading(install, evidence, material));
        });
    }

    private static GameAssetRef? ShadingMaterial(GameVfs vfs, SubjectMaterial material) =>
        material.PathId == 0 || string.IsNullOrWhiteSpace(material.Bundle) ? null : new GameAssetRef
        {
            GameBuild = vfs.CatalogVersion, LogicalBundle = material.Bundle,
            PathId = material.PathId, Name = material.Name,
        };

    /// <summary>Finish material capabilities before the model is published as ready. Shared materials
    /// across parts and outfits converge on the same cache entry.</summary>
    internal SubjectModel WarmSubjectShading(GameVfs vfs, SubjectModel model)
    {
        var cache = MaterialShadingFor(vfs);
        foreach (var material in model.Parts.SelectMany(part => part.Materials))
            if (ShadingMaterial(vfs, material) is { } reference) cache.GetOrRead(reference);
        return model;
    }

    private SubjectModel PrepareCurrentSubject(GameVfs vfs, SubjectModel model)
    {
        var prepared = WarmSubjectShading(vfs, model);
        if (!ReferenceEquals(vfs, _vfs)) throw new InvalidOperationException(EditPageVm.ShadingInstallUnavailable);
        return prepared;
    }

    private static byte[]? ReadSubjectBundle(GameVfs vfs, string logical)
    {
        try { return vfs.TryDeobfuscateLogical(logical); }
        catch { return null; }
    }

    public EditShadingRead? PeekShading(TargetPart part, int materialSlotIndex,
        GameAssetRef? material = null)
    {
        if (_vfs is not { } vfs) return new EditShadingRead(null, EditPageVm.ShadingInstallUnavailable);
        var model = SubjectPartOf(part);
        if (model is null)
        {
            var state = SubjectReadState(part.Subject, part.Outfit);
            return state == EditSubjectRead.Reading ? null : new EditShadingRead(null,
                state == EditSubjectRead.Unreadable ? GameFilesGate.SubjectUnreadable
                    : MaterialShadingCache.ReadFailure);
        }
        var selected = model.Materials.ElementAtOrDefault(materialSlotIndex);
        if (selected is null || selected.IsPlaceholder) return new EditShadingRead(null);
        // A saved slot may still name the previous game's material. The loaded outfit owns the
        // current position, exactly as it does for the numeric editor and build resolver.
        material = ShadingMaterial(vfs, selected);
        if (material is null) return new EditShadingRead(null, MaterialShadingCache.ReadFailure);
        // Do not take the factory/evidence lock: a card must never wait for an in-flight bundle read.
        return _materialShading.TryGetValue(vfs, out var cache) && cache.Peek(material) is { } answer
            ? answer : new EditShadingRead(null, MaterialShadingCache.ReadFailure);
    }
}
