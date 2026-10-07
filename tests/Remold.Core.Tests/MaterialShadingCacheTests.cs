using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.App.ViewModels.EditPage;
using Remold.Core;
using Remold.Core.Bundles;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

[Collection("Dispatcher")]
public sealed class MaterialShadingCacheTests
{
    private const string CharacterName = "Vesna";
    private const string OutfitStem = "VesnaSSR01";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Parallel_case_variants_share_one_read_and_peek_never_waits_for_it()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = Answer();
        int calls = 0;
        var cache = new MaterialShadingCache(_ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            if (!release.Wait(Deadline)) throw new TimeoutException("fixture reader was not released");
            return answer;
        });
        Assert.Null(cache.Peek(Material()));
        Assert.Equal(0, calls);
        var first = Task.Run(() => cache.GetOrRead(Material()));
        Task<EditShadingRead>[] peers = Array.Empty<Task<EditShadingRead>>();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            var peek = Task.Run(() => cache.Peek(Material("SHARED.BUNDLE")));
            Assert.Null(await peek.WaitAsync(Deadline));
            peers = Enumerable.Range(0, 8).Select(index => Task.Run(() => cache.GetOrRead(
                Material(index % 2 == 0 ? "shared.bundle" : "SHARED.BUNDLE")))).ToArray();
        }
        finally { release.Set(); }

        Assert.Same(answer, await first.WaitAsync(Deadline));
        foreach (var result in await Task.WhenAll(peers).WaitAsync(Deadline)) Assert.Same(answer, result);
        Assert.Equal(1, calls);
        Assert.Same(answer, cache.Peek(Material("SHARED.BUNDLE")));

        cache.GetOrRead(Material(pathId: 42));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_and_cancelled_reads_settle_once_without_a_redraw_retry(bool cancelled)
    {
        int calls = 0;
        var cache = new MaterialShadingCache(_ =>
        {
            calls++;
            if (cancelled) throw new OperationCanceledException();
            throw new IOException("fixture read error");
        });

        var first = cache.GetOrRead(Material());

        Assert.Null(first.Info);
        Assert.Equal(MaterialShadingCache.ReadFailure, first.Problem);
        Assert.Same(first, cache.Peek(Material()));
        Assert.Same(first, cache.GetOrRead(Material("SHARED.BUNDLE")));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Effect_presence_survives_an_answer_without_adjustable_values()
    {
        int calls = 0;
        var answer = new EditShadingRead(null, PresentEffects: new[] { "face-shading" });
        var cache = new MaterialShadingCache(_ => { calls++; return answer; });

        cache.GetOrRead(Material());

        var peek = cache.Peek(Material());
        Assert.NotNull(peek);
        Assert.Null(peek.Info);
        Assert.Null(peek.Problem);
        Assert.Equal(new[] { "face-shading" }, peek.PresentEffects);
        Assert.Same(answer, cache.GetOrRead(Material()));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_late_old_install_read_does_not_replace_the_current_install_cache()
    {
        using var temp = new TempGame();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstInstall = Install(temp.At("first-install"));
        var secondInstall = Install(temp.At("second-install"));
        var firstAnswer = Answer("detail");
        var secondAnswer = Answer("stocking");
        int firstCalls = 0, secondCalls = 0;
        var vm = new MainWindowViewModel(startLoad: false, cacheRootFor: () => temp.At("cache"));
        vm.MaterialShadingReadForTest = (install, _) =>
        {
            if (ReferenceEquals(install, firstInstall))
            {
                Interlocked.Increment(ref firstCalls);
                entered.TrySetResult();
                if (!release.Wait(Deadline)) throw new TimeoutException("fixture reader was not released");
                return firstAnswer;
            }
            Assert.Same(secondInstall, install);
            Interlocked.Increment(ref secondCalls);
            return secondAnswer;
        };
        var firstCache = vm.MaterialShadingFor(firstInstall);
        var pending = Task.Run(() => firstCache.GetOrRead(Material()));
        MaterialShadingCache? current = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            current = vm.MaterialShadingFor(secondInstall);
            Assert.NotSame(firstCache, current);
            Assert.Null(current.Peek(Material()));
            Assert.Same(secondAnswer, current.GetOrRead(Material()));
        }
        finally { release.Set(); }

        Assert.Same(firstAnswer, await pending.WaitAsync(Deadline));
        Assert.Same(current, vm.MaterialShadingFor(secondInstall));
        Assert.Same(secondAnswer, vm.MaterialShadingFor(secondInstall).Peek(Material()));
        Assert.Same(firstCache, vm.MaterialShadingFor(firstInstall));
        Assert.Same(current, vm.MaterialShadingFor(secondInstall));
        Assert.Equal(1, firstCalls);
        Assert.Equal(1, secondCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clearing_subject_models_prevents_an_old_build_from_repopulating_them(bool publishCurrent)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new SubjectModelCache();
        var old = Model();
        var current = Model();
        var pending = Task.Run(() => cache.GetOrBuild(CharacterName, OutfitStem, () =>
        {
            entered.TrySetResult();
            if (!release.Wait(Deadline)) throw new TimeoutException("fixture model was not released");
            return old;
        }));
        try
        {
            await entered.Task.WaitAsync(Deadline);
            cache.Clear();
            if (publishCurrent) cache.GetOrBuild(CharacterName, OutfitStem, () => current);
        }
        finally { release.Set(); }

        Assert.Same(old, await pending.WaitAsync(Deadline));
        if (publishCurrent) Assert.Same(current, cache.TryGet(CharacterName, OutfitStem));
        else Assert.Null(cache.TryGet(CharacterName, OutfitStem));
    }

    [Fact]
    public void Peek_distinguishes_a_loading_subject_from_unreadable_or_missing_materials()
    {
        using var temp = new TempGame();
        var vm = new MainWindowViewModel(startLoad: false, cacheRootFor: () => temp.At("cache"))
            { IsScanning = false };
        vm.SetLoadedInstallForTest(Install(temp.At("install")), temp.Root, Array.Empty<Character>());
        int reads = 0;
        vm.MaterialShadingReadForTest = (_, _) => { reads++; return Answer(); };

        Assert.Null(vm.PeekShading(Part(), 0));
        vm.SubjectModels.MarkUnreadable(CharacterName, OutfitStem);
        Assert.Equal(GameFilesGate.SubjectUnreadable, vm.PeekShading(Part(), 0)?.Problem);

        vm.SubjectModels.Clear();
        vm.SubjectModels.GetOrBuild(CharacterName, OutfitStem, () => new SubjectModel(
            CharacterName, OutfitStem, SubjectSource.Prefab, Array.Empty<SubjectPart>(),
            Skeleton: null, Problems: Array.Empty<string>()));
        Assert.Equal(MaterialShadingCache.ReadFailure, vm.PeekShading(Part(), 0)?.Problem);
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adding_or_opening_an_outfit_waits_for_all_material_answers_before_publication(bool openMod)
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var install = Install(temp.At("install"));
        var model = Model();
        var answer = Answer();
        int reads = 0;
        var vm = new MainWindowViewModel(startLoad: false, cacheRootFor: () => temp.At("cache"),
            subjectModelWarm: (_, _) => model, pageDispatch: work => work());
        var outfit = new Outfit(1, OutfitStem, OutfitKind.Alt);
        var character = new Character(1, CharacterName, CharacterName, 1, 1, new List<Outfit> { outfit });
        vm.SetLoadedInstallForTest(install, temp.Root, new[] { character });
        vm.MaterialShadingReadForTest = (_, material) =>
        {
            Interlocked.Increment(ref reads);
            if (material.PathId == 41)
            {
                entered.TrySetResult();
                if (!release.Wait(Deadline)) throw new TimeoutException("fixture reader was not released");
            }
            return answer;
        };
        long before = vm.SubjectModels.Version;
        try
        {
            if (openMod)
            {
                const string name = "Shading cache fixture";
                string root = temp.At(name);
                Directory.CreateDirectory(root);
                var project = new AuthoredProject { WorkspaceIndex = new AuthoredWorkspaceIndex() };
                project.Info.Name = name;
                project.WorkspaceIndex.Selection.Add(new SelectionEntry
                    { Character = CharacterName, Outfit = OutfitStem });
                AuthoredProjectSerializer.Save(project, ModProject.ManifestPathFor(root));
                Assert.True(await vm.OpenModAsync(root));
            }
            else
            {
                var row = new CharacterVm(character, (_, _) => { }, (_, _) => { });
                row.Populate(new[] { (outfit, (IEnumerable<string>)new[] { "body" }) });
                vm.AddSubject(row, Assert.Single(row.Outfits));
            }
            await entered.Task.WaitAsync(Deadline);
            Assert.Null(vm.SubjectModels.TryGet(CharacterName, OutfitStem));
            Assert.Null(vm.MaterialShadingFor(install).Peek(Material()));
        }
        finally { release.Set(); }

        await vm.SubjectModels.WaitForChangeAsync(before, CancellationToken.None).WaitAsync(Deadline);
        Assert.Same(model, vm.SubjectModels.TryGet(CharacterName, OutfitStem));
        Assert.Equal(2, reads);
        Assert.Same(answer, vm.MaterialShadingFor(install).Peek(Material()));
        Assert.Same(answer, vm.MaterialShadingFor(install).Peek(Material(pathId: 42)));
        var stale = Material("previous.bundle", 999);
        Assert.Same(answer, vm.PeekShading(Part(), 0, stale));
        vm.WarmSubjectShading(install, model);
        Assert.Equal(2, reads);
    }

    private static EditShadingRead Answer(string effectId = "detail") => new(
        new EditShadingInfo(Array.Empty<EditShadingField>(), new[] { new EditShadingEffect(effectId, effectId) }),
        PresentEffects: new[] { effectId });

    private static GameAssetRef Material(string bundle = "shared.bundle", long pathId = 41) => new()
        { GameBuild = "fixture", LogicalBundle = bundle, PathId = pathId, Name = "coat_material" };

    private static GameVfs Install(string root) => TestVfs.Create(root,
        Array.Empty<(string Address, string OwnerBundle)>(), null,
        ("shared.bundle", new string('1', 32)));

    private static TargetPart Part() => new()
        { Subject = CharacterName, Outfit = OutfitStem, RendererSlot = "body" };

    private static SubjectModel Model() => new(CharacterName, OutfitStem, SubjectSource.Prefab,
        new[]
        {
            new SubjectPart("body", "body", "body-address", new[]
            {
                new SubjectMaterial("coat_material", 41, "CAB-fixture", Array.Empty<SubjectMap>(),
                    Bundle: "shared.bundle"),
                new SubjectMaterial("lining_material", 42, "CAB-fixture", Array.Empty<SubjectMap>(),
                    Bundle: "shared.bundle"),
            }),
            new SubjectPart("trim", "trim", "trim-address", new[]
            {
                new SubjectMaterial("coat_material", 41, "CAB-fixture", Array.Empty<SubjectMap>(),
                    Bundle: "SHARED.BUNDLE"),
            }),
        }, Skeleton: null, Problems: Array.Empty<string>());
}
