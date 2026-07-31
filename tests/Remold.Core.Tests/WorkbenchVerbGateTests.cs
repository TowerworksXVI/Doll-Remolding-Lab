using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.App.ViewModels.Workbench;
using Remold.Core.Export;
using Remold.Core.Migoto;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tables;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// One verb runs at a time ACROSS the whole tree, but the buttons disable per NODE — so while one node's
/// verb is open, a second node's button still looks live. These pin what a click on it does: the refusal is
/// SAID on the status line, never swallowed into a button that appears to do nothing.
/// </summary>
public class WorkbenchVerbGateTests
{
    /// <summary>A shell whose verbs block until the test releases them, so the gate can be observed held.</summary>
    private sealed class GateShell : IWorkbenchShell
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OpenMapCalls;
        public int RemoveCalls;
        public int OpenPartCalls;
        public int OpenPartAloneCalls;
        public int AutoSaveCalls;

        public void Release() => _open.TrySetResult();

        public Task OpenMapInEditorAsync(WorkbenchSubjectRef s, string t, string b, IReadOnlyList<string> o, IProgress<string> p)
        {
            OpenMapCalls++;
            return _open.Task;
        }

        public Task RemoveSubjectAsync(WorkbenchSubjectRef s)
        {
            RemoveCalls++;
            return _open.Task;
        }

        // ---- unused by these gate tests ----
        public Task<bool> ConfirmApplyDroppedPngAsync(DroppedPngConfirm ask) => Task.FromResult(false);
        public Task ApplyDroppedPngToDonorMapAsync(WorkbenchSubjectRef s, DonorMapDrop d, string r, string p, IProgress<string> st) => Task.CompletedTask;
        public Task ApplyDroppedPngAsync(WorkbenchSubjectRef s, string t, string b, IReadOnlyList<string> o, string path, IProgress<string> p) => Task.CompletedTask;
        public Task ApplyDroppedPngToAuthoredAsync(string authoredPath, string part, string role, string path, IProgress<string> p) => Task.CompletedTask;
        public Task<PartMaterializeOutcome> MaterializePartAsync(WorkbenchSubjectRef s, RecipePart r, IProgress<string> p, CancellationToken c) => Task.FromResult(PartMaterializeOutcome.Ready());
        public Task<bool> MaterializeTextureAsync(WorkbenchSubjectRef s, string t, string b, IReadOnlyList<string> o, IProgress<string> p, CancellationToken c) => Task.FromResult(true);
        public Task OpenPartInBlenderAsync(WorkbenchSubjectRef s, RecipePart r, IReadOnlyList<RecipePart> outfit, IProgress<string> p)
        {
            OpenPartCalls++;
            return Task.CompletedTask;
        }

        public Task OpenPartAloneInBlenderAsync(WorkbenchSubjectRef s, RecipePart r, IProgress<string> p)
        {
            OpenPartAloneCalls++;
            return Task.CompletedTask;
        }

        public Task OpenAllPartsInBlenderAsync(WorkbenchSubjectRef s, IReadOnlyList<RecipePart> r, IProgress<string> p) => Task.CompletedTask;
        public Task OpenAuthoredMapAsync(string authoredPath, IProgress<string> p) => Task.CompletedTask;
        public Task<int> MaterializeAllAsync(WorkbenchSubjectRef s, IReadOnlyList<MaterializeItem> i, IProgress<string> p, CancellationToken c) => Task.FromResult(0);
        public Task RevertPartAsync(WorkbenchSubjectRef s, string t, IProgress<string> p) => Task.CompletedTask;
        public Task OpenMapUvGuideAsync(WorkbenchSubjectRef s, string t, string b, IReadOnlyList<(string, string, int, string?)> u, IProgress<string> p) => Task.CompletedTask;
        public Task RevertMapAsync(WorkbenchSubjectRef s, string t, string b, IProgress<string> p) => Task.CompletedTask;
        public void PrewarmSubject(WorkbenchSubjectRef s) { }
        public void ShowSubjectInFolder(WorkbenchSubjectRef s) { }
        public Task CopyTextAsync(string? text) => Task.CompletedTask;
        public void GoToBuild() { }
        public void AutoSaveProject() => AutoSaveCalls++;
    }

    private static readonly WorkbenchSubjectRef Subject =
        new("char", "stem", "c_stem_slg_", new Outfit(0, "stem", OutfitKind.Base));

    private static WorkbenchVm NewVm(GateShell shell) => new(
        project: () => new ModProject(),
        vfs: () => null,
        friendly: () => FriendlyNames.Empty,
        roster: () => Array.Empty<Character>(),
        tryDeobfuscate: _ => null,
        catalog: null,
        shell: shell);

    private static WorkbenchMapVm Card(string textureName) =>
        new("Base", "_MainTex", textureName, "bundle1") { Subject = Subject };

    [Fact]
    public async Task ASecondCardsVerbRefusedByTheOpenOneSaysSo()
    {
        var shell = new GateShell();
        var vm = NewVm(shell);
        var held = Card("tex_face");
        var other = Card("tex_body");

        var running = vm.OpenMapCommand.ExecuteAsync(held);
        Assert.False(running.IsCompleted);                  // the first verb holds the gate

        await vm.OpenMapCommand.ExecuteAsync(other);        // the OTHER card's button never disabled

        Assert.Equal(1, shell.OpenMapCalls);                // refused before the shell
        Assert.Contains("Busy", vm.Status);                 // …and the click is not swallowed

        shell.Release();
        await running;
    }

    [Fact]
    public async Task ASubjectVerbRefusedByAnOpenMapVerbSaysSo()
    {
        // The gate spans node KINDS too: a map open in flight refuses a subject's Remove on the tree.
        var shell = new GateShell();
        var vm = NewVm(shell);
        var node = new WorkbenchNodeVm { Kind = WorkbenchNodeKind.Subject, Title = "subject", Subject = Subject };

        var running = vm.OpenMapCommand.ExecuteAsync(Card("tex_face"));
        Assert.False(running.IsCompleted);

        await vm.RemoveSubjectCommand.ExecuteAsync(node);

        Assert.Equal(0, shell.RemoveCalls);
        Assert.Contains("Busy", vm.Status);

        shell.Release();
        await running;
    }

    [Fact]
    public async Task TheGateReopensWhenTheVerbFinishes()
    {
        // The refusal is a "not now", so the same click has to work once the gate is back.
        var shell = new GateShell();
        var vm = NewVm(shell);
        var other = Card("tex_body");

        var running = vm.OpenMapCommand.ExecuteAsync(Card("tex_face"));
        await vm.OpenMapCommand.ExecuteAsync(other);
        Assert.Equal(1, shell.OpenMapCalls);

        shell.Release();
        await running;
        await vm.OpenMapCommand.ExecuteAsync(other);

        Assert.Equal(2, shell.OpenMapCalls);
    }

    // ---- shell work taking the same gate ----------------------------------------------------------
    // Refusing later verbs is only half the exclusion. A send-back apply overwrites the very files an
    // already-open verb is writing, so it has to WAIT for that one rather than start beside it.

    [Fact]
    public async Task ShellWorkWaitsForTheVerbAlreadyInFlight()
    {
        var shell = new GateShell();
        var vm = NewVm(shell);

        var running = vm.OpenMapCommand.ExecuteAsync(Card("tex_face"));
        Assert.False(running.IsCompleted);

        var hold = vm.HoldVerbsAsync();
        Assert.False(hold.IsCompleted);      // it waits rather than writing over the open verb

        shell.Release();
        await running;
        using var held = await hold;         // …and lands the moment the verb lets go
    }

    [Fact]
    public async Task ShellWorkTakesTheGateAtOnceWhenNoVerbIsRunning()
    {
        var vm = NewVm(new GateShell());

        var hold = vm.HoldVerbsAsync();

        Assert.True(hold.IsCompleted);
        (await hold).Dispose();
    }

    [Fact]
    public async Task AVerbClickedWhileShellWorkWaitsIsStillRefused()
    {
        // The wait must not open a window for new verbs to join: the refusal starts with the hold, exactly as
        // it does when nothing had to be waited for.
        var shell = new GateShell();
        var vm = NewVm(shell);
        var running = vm.OpenMapCommand.ExecuteAsync(Card("tex_face"));
        var hold = vm.HoldVerbsAsync();

        await vm.OpenMapCommand.ExecuteAsync(Card("tex_body"));

        Assert.Equal(1, shell.OpenMapCalls);
        Assert.Contains("Busy", vm.Status);

        shell.Release();
        await running;
        using var held = await hold;
    }

    // ---- a part whose mesh can't be replaced ------------------------------------------------------

    private static WorkbenchVm NewVm(GateShell shell, ModProject project) => new(
        project: () => project,
        vfs: () => null,
        friendly: () => FriendlyNames.Empty,
        roster: () => Array.Empty<Character>(),
        tryDeobfuscate: _ => null,
        catalog: null,
        shell: shell);

    private static WorkbenchNodeVm PartNode(string token, bool isStatic = false) => new()
    {
        Kind = WorkbenchNodeKind.Part,
        Title = token,
        PartToken = token,
        Subject = Subject,
        IsStaticPart = isStatic,
        Recipe = new RecipePart(token, $"c_stem_slg_{token}_lod0", $"addr/{token}",
            Array.Empty<RecipeTierSlot>()),
    };

    [Fact]
    public async Task AnUnreplaceablePartsOpenVerbRefusesWithTheReasonTheButtonCarries()
    {
        var shell = new GateShell();
        var vm = NewVm(shell);
        var face = PartNode("face");
        face.MeshReplaceBlock = StreamDump.SkinRefusal.BlendShapes;

        Assert.False(face.CanOpenInBlender);
        Assert.Equal("This mesh uses expressions and cannot be replaced.", face.BlenderHint);

        await vm.OpenPartInBlenderCommand.ExecuteAsync(face);

        Assert.Equal(0, shell.OpenPartCalls);                        // refused before the shell
        Assert.Equal(face.BlenderHint, vm.Status);                   // …and it is SAID, not swallowed
    }

    [Fact]
    public async Task AClickThatBeatsTheSkinReadStillRefuses()
    {
        // Selection starts the read; the button reads enabled until it lands. A click inside that window
        // must answer on the mesh, not on how fast it arrived — so the verb waits for the read it started
        // rather than opening on a mesh nothing has read yet.
        var shell = new GateShell();
        var vm = NewVm(shell);
        var face = PartNode("face");
        var settle = new TaskCompletionSource<StreamDump.SkinRefusal?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        face.MeshReplaceGate = settle.Task;                          // in flight: nothing assigned yet

        Assert.Null(face.MeshReplaceBlock);
        Assert.True(face.CanOpenInBlender);                          // the button has not caught up

        var clicked = vm.OpenPartInBlenderCommand.ExecuteAsync(face);
        Assert.False(clicked.IsCompleted);                           // it waits rather than opening
        Assert.Equal(0, shell.OpenPartCalls);

        settle.SetResult(StreamDump.SkinRefusal.BlendShapes);
        await clicked;

        Assert.Equal(0, shell.OpenPartCalls);                        // the answer arrived and refused
        Assert.Equal("This mesh uses expressions and cannot be replaced.", vm.Status);
    }

    [Fact]
    public async Task AClickThatBeatsTheSkinReadOnAReplaceablePartStillOpens()
    {
        // The same wait must not become a refusal by default: a part the read clears opens as it always did.
        var shell = new GateShell();
        var vm = NewVm(shell);
        var hair = PartNode("hair");
        var settle = new TaskCompletionSource<StreamDump.SkinRefusal?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        hair.MeshReplaceGate = settle.Task;

        var clicked = vm.OpenPartInBlenderCommand.ExecuteAsync(hair);
        settle.SetResult(null);
        await clicked;

        Assert.Equal(1, shell.OpenPartCalls);
    }

    [Fact]
    public async Task AReadableSkinPartsOpenVerbRunsAsBefore()
    {
        var shell = new GateShell();
        var vm = NewVm(shell);
        var hair = PartNode("hair");

        Assert.True(hair.CanOpenInBlender);

        await vm.OpenPartInBlenderCommand.ExecuteAsync(hair);

        Assert.Equal(1, shell.OpenPartCalls);
    }

    // ---- the part's two opens ---------------------------------------------------------------------

    /// <summary>The references-free open is its own verb, so a click on it reaches the shell entry that skips
    /// the outfit build rather than the one that waits on it.</summary>
    [Fact]
    public async Task ThePartsSecondOpenVerbGoesToTheLoneEntry()
    {
        var shell = new GateShell();
        var vm = NewVm(shell);
        var hair = PartNode("hair");

        await vm.OpenPartAloneInBlenderCommand.ExecuteAsync(hair);

        Assert.Equal(1, shell.OpenPartAloneCalls);
        Assert.Equal(0, shell.OpenPartCalls);
    }

    /// <summary>Both part opens replace the same game mesh, so the mesh's own refusal reaches both. A rule
    /// enforced on one button is a rule the other one walks around.</summary>
    [Fact]
    public async Task AnUnreplaceablePartRefusesTheLoneOpenToo()
    {
        var shell = new GateShell();
        var vm = NewVm(shell);
        var face = PartNode("face");
        face.MeshReplaceBlock = StreamDump.SkinRefusal.SkinLayout;

        Assert.False(face.CanOpenInBlender);                         // one gate drives both buttons
        Assert.Equal(face.BlenderHint, face.BlenderAloneHint);        // …and both say why on hover

        await vm.OpenPartAloneInBlenderCommand.ExecuteAsync(face);

        Assert.Equal(0, shell.OpenPartAloneCalls);
        Assert.Equal(BlenderGate.Blocked(StreamDump.SkinRefusal.SkinLayout), vm.Status);
    }

    /// <summary>The two buttons hover differently only in what they say the verb DOES — which is the whole
    /// choice being offered.</summary>
    [Fact]
    public void TheTwoPartOpensDescribeThemselvesApart()
    {
        var hair = PartNode("hair");

        Assert.Equal(BlenderGate.ReadyPart, hair.BlenderHint);
        Assert.Equal(BlenderGate.ReadyPartAlone, hair.BlenderAloneHint);
    }

    // ---- a part's live Blender session ------------------------------------------------------------

    /// <summary>While a session opened from a part's row lives, BOTH of that part's opens are off and say
    /// why. A second session on one part sends back to the same file, so the last Send would take it with
    /// nothing on screen having said a word.</summary>
    [Fact]
    public async Task APartWithALiveSessionRefusesBothOfItsOpens()
    {
        var shell = new GateShell();
        var vm = NewVm(shell);
        var hair = PartNode("hair");
        hair.IsOpenInBlender = true;

        Assert.False(hair.CanOpenInBlender);
        Assert.Equal(BlenderGate.AlreadyOpen, hair.BlenderHint);
        Assert.Equal(BlenderGate.AlreadyOpen, hair.BlenderAloneHint);

        await vm.OpenPartInBlenderCommand.ExecuteAsync(hair);
        await vm.OpenPartAloneInBlenderCommand.ExecuteAsync(hair);

        Assert.Equal(0, shell.OpenPartCalls);
        Assert.Equal(0, shell.OpenPartAloneCalls);
        Assert.Equal(BlenderGate.AlreadyOpen, vm.Status);   // said, not swallowed into a dead button
    }

    /// <summary>Blender exits and the part is openable again, on the same state the shell pushed to close
    /// it — a refusal with no way out of it is a part the modder can never open twice.</summary>
    [Fact]
    public async Task TheSessionEnding_GivesThePartsOpensBack()
    {
        var shell = new GateShell();
        var vm = NewVm(shell);
        var hair = PartNode("hair");
        vm.Nodes.Add(SubjectNode(hair));

        vm.SetPartSession(Subject, "hair", alive: true);
        Assert.False(hair.CanOpenInBlender);

        vm.SetPartSession(Subject, "hair", alive: false);

        Assert.True(hair.CanOpenInBlender);
        Assert.Equal(BlenderGate.ReadyPart, hair.BlenderHint);
        Assert.Equal(BlenderGate.ReadyPartAlone, hair.BlenderAloneHint);
        await vm.OpenPartAloneInBlenderCommand.ExecuteAsync(hair);
        Assert.Equal(1, shell.OpenPartAloneCalls);
    }

    /// <summary>The session belongs to the ROW it was opened from. Every other part of the outfit stays
    /// openable — one part in Blender is not the outfit locked.</summary>
    [Fact]
    public void ALiveSessionLeavesEveryOtherPartOpenable()
    {
        var vm = NewVm(new GateShell());
        var hair = PartNode("hair");
        var body = PartNode("body");
        vm.Nodes.Add(SubjectNode(hair, body));

        vm.SetPartSession(Subject, "hair", alive: true);

        Assert.False(hair.CanOpenInBlender);
        Assert.True(body.CanOpenInBlender);
        Assert.Equal(BlenderGate.ReadyPart, body.BlenderHint);
    }

    private static WorkbenchNodeVm SubjectNode(params WorkbenchNodeVm[] parts)
    {
        var root = new WorkbenchNodeVm { Kind = WorkbenchNodeKind.Subject, Title = "stem", Subject = Subject };
        foreach (var p in parts) root.Children.Add(p);
        return root;
    }

    [Fact]
    public void TheReducedLayoutBranchGetsItsOwnLine()
    {
        var body = PartNode("body");
        body.MeshReplaceBlock = StreamDump.SkinRefusal.SkinLayout;

        Assert.False(body.CanOpenInBlender);
        Assert.Equal("This mesh's skin is reduced, and replacement needs a full poseable one. Hide and retexture still work.", body.BlenderHint);
    }

    [Fact]
    public void TheSubjectsOpenAllStaysLiveBesideAnUnreplaceablePart()
    {
        // Open-all opens a COMBINED session; the unreplaceable part rides it as context, so the subject's
        // own verb is never the thing refused.
        var subject = new WorkbenchNodeVm { Kind = WorkbenchNodeKind.Subject, Title = "subject", Subject = Subject };
        var face = PartNode("face");
        face.MeshReplaceBlock = StreamDump.SkinRefusal.BlendShapes;
        subject.Children.Add(face);

        Assert.True(subject.CanOpenInBlender);
        Assert.Equal(BlenderGate.ReadyAll, subject.BlenderHint);
    }

    [Fact]
    public async Task AnAllStaticSubjectsOpenAllIsGatedAndSaysWhy()
    {
        // Static parts are authored one at a time: the combined session carries the SKINNED parts, so a
        // subject made only of static ones has no session to open.
        var shell = new GateShell();
        var vm = NewVm(shell);
        var subject = new WorkbenchNodeVm
        {
            Kind = WorkbenchNodeKind.Subject, Title = "prop", Subject = Subject, AllPartsStatic = true,
        };
        subject.Children.Add(PartNode("frame"));

        Assert.False(subject.CanOpenInBlender);
        Assert.Equal("Static parts open one at a time. Select a part and use Open in Blender.",
            subject.BlenderHint);

        await vm.OpenAllPartsCommand.ExecuteAsync(subject);

        Assert.Equal(subject.BlenderHint, vm.Status);   // refused, and SAID rather than swallowed
    }

    // ---- a static part's two opens ----------------------------------------------------------------
    // The references session IS the combined rigged glb, and only skinned parts join one. So the part row's
    // two opens stop sharing a gate: the references open refuses, the lone one is how a static part is
    // authored.

    [Fact]
    public async Task AStaticPartsReferencesOpenIsGatedAndSaysWhy()
    {
        var shell = new GateShell();
        var vm = NewVm(shell);
        var frame = PartNode("frame", isStatic: true);

        Assert.False(frame.CanOpenWithReferences);
        Assert.Equal("Static parts open on their own. Use Open in Blender.", frame.ReferencesHint);

        await vm.OpenPartInBlenderCommand.ExecuteAsync(frame);

        Assert.Equal(0, shell.OpenPartCalls);                // refused before the shell
        Assert.Equal(frame.ReferencesHint, vm.Status);       // …and SAID rather than swallowed
    }

    [Fact]
    public async Task AStaticPartsLoneOpenStaysLive()
    {
        // The refusal is about the SESSION, not the part: on its own it opens exactly as it always did.
        var shell = new GateShell();
        var vm = NewVm(shell);
        var frame = PartNode("frame", isStatic: true);

        Assert.True(frame.CanOpenInBlender);
        Assert.Equal(BlenderGate.ReadyPartAlone, frame.BlenderAloneHint);

        await vm.OpenPartAloneInBlenderCommand.ExecuteAsync(frame);

        Assert.Equal(1, shell.OpenPartAloneCalls);
    }

    [Fact]
    public async Task ASkinnedPartKeepsBothOpens()
    {
        var shell = new GateShell();
        var vm = NewVm(shell);
        var hair = PartNode("hair");

        Assert.True(hair.CanOpenWithReferences);
        Assert.True(hair.CanOpenInBlender);
        Assert.Equal(BlenderGate.ReadyPart, hair.ReferencesHint);

        await vm.OpenPartInBlenderCommand.ExecuteAsync(hair);
        await vm.OpenPartAloneInBlenderCommand.ExecuteAsync(hair);

        Assert.Equal(1, shell.OpenPartCalls);
        Assert.Equal(1, shell.OpenPartAloneCalls);
    }

    /// <summary>The static rule is one arm of the same ordered gate, so a mesh refusal still outranks it: the
    /// line a part shows names the thing that can never be worked around, not the second one down.</summary>
    [Fact]
    public void AnUnreplaceableStaticPartLeadsWithTheMeshRefusal()
    {
        var frame = PartNode("frame", isStatic: true);
        frame.MeshReplaceBlock = StreamDump.SkinRefusal.BlendShapes;

        Assert.Equal(BlenderGate.BlendShapes, frame.ReferencesHint);
    }

    /// <summary>The static answer reaches the node from the subject model, where the prefab's renderer class
    /// settled it — a gate the tree never fills in is a gate that never fires.</summary>
    [Fact]
    public void TheTreeCarriesEachPartsStaticAnswerDown()
    {
        var model = new SubjectModel("char", "stem", SubjectSource.Prefab, new[]
        {
            new SubjectPart("frame", "c_stem_slg_frame_lod0", "addr/frame",
                Array.Empty<SubjectMaterial>(), IsStatic: true),
            new SubjectPart("hair", "c_stem_slg_hair_lod0", "addr/hair",
                Array.Empty<SubjectMaterial>()),
        }, Skeleton: null, Problems: Array.Empty<string>());

        var root = WorkbenchVm.BuildSubjectNode("stem", model, Subject);
        var parts = root.Children.Where(c => c.Kind == WorkbenchNodeKind.Part).ToList();

        Assert.True(parts[0].IsStaticPart);
        Assert.False(parts[0].CanOpenWithReferences);
        Assert.True(parts[0].CanOpenInBlender);
        Assert.False(parts[1].IsStaticPart);
        Assert.True(parts[1].CanOpenWithReferences);
    }

    [Fact]
    public void AMixedSubjectsOpenAllStaysLive()
    {
        // Some parts static, some skinned: the session simply carries the skinned ones, as it always has.
        var subject = new WorkbenchNodeVm
        {
            Kind = WorkbenchNodeKind.Subject, Title = "subject", Subject = Subject, AllPartsStatic = false,
        };

        Assert.True(subject.CanOpenInBlender);
        Assert.Equal(BlenderGate.ReadyAll, subject.BlenderHint);
    }

    [Fact]
    public void HideAndTheTextureAffordancesOfAnUnreplaceablePartStayLive()
    {
        // The gate is about REPLACING the mesh. Retexture and Hide of these parts work, and must keep
        // working — pin that the gate reaches neither.
        var shell = new GateShell();
        var project = new ModProject();
        var vm = NewVm(shell, project);
        var face = PartNode("face");
        face.MeshReplaceBlock = StreamDump.SkinRefusal.BlendShapes;
        var card = new WorkbenchMapVm("Base", "_MainTex", "tex_face", "bundle1") { Subject = Subject };
        face.Maps.Add(card);

        vm.ToggleHiddenCommand.Execute(face);
        Assert.True(face.IsHiddenInMod);
        Assert.Equal(1, shell.AutoSaveCalls);

        Assert.True(card.CanOpenUvGuide);                     // Open map + UV guide unaffected
        _ = vm.OpenMapCommand.ExecuteAsync(card);             // the map verb reaches the shell
        Assert.Equal(1, shell.OpenMapCalls);

        shell.Release();
    }
}
