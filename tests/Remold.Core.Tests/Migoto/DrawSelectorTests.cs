using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Remold.Core.Bundles;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests.Migoto;

public class DrawSelectorTests
{
    static readonly DrawSelector A = new("00112233", "11223344", "22334455", "33445566");
    static readonly DrawSelector B = A with { Vb2 = "44556677" };

    [Fact]
    public void Original_slot_two_bytes_add_information_without_changing_the_old_hashes()
    {
        string root = Path.Combine(Path.GetTempPath(), "selector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "mesh.bundle");
            SyntheticBundle.BuildOneSkinnedMesh(path, "mesh", new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                new[] { 0, 1, 2 }, new uint[] { 7, 9 });
            var field = new BundleReader().GetMeshField(File.ReadAllBytes(path), "mesh")!;
            var raw = MeshRaw.From(field);
            var before = BufferHash.Compute(raw);
            int offset = 0;
            for (int i = 0; i < raw.StreamIds.IndexOf(2); i++) offset += (raw.VertexCount * raw.Stride(i) + 15) & ~15;
            raw.VData[offset] ^= 1;
            var after = BufferHash.Compute(raw);
            Assert.Equal(before.Ib, after.Ib);
            Assert.Equal(before.Vb0, after.Vb0);
            Assert.Equal(before.Vb1, after.Vb1);
            Assert.NotEqual(before.Vb2, after.Vb2);
            Assert.False(before.Selector.Overlaps(after.Selector));

            raw.StreamIds[1] = 2;
            raw.StreamIds[2] = 3;
            var sparse = BufferHash.Compute(raw);
            Assert.Null(sparse.Vb1);
            Assert.Equal(before.Vb1, sparse.Vb2);
            raw.StreamIds[1] = 1;
            Assert.Null(BufferHash.Compute(raw).Vb2);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Compound_identity_does_not_depend_on_a_companion_or_remember_the_previous_draw()
    {
        string ini = DrawSelectorIni.Lower($"[TextureOverride_A]\nhash = {A.Key}\nmatch_priority = 0\n"
            + "$seenA = 1\nhandling = skip\nrun = CommandListA\n\n"
            + $"[TextureOverride_B]\nhash = {B.Key}\nmatch_priority = 0\n"
            + "$seenB = 1\nhandling = skip\nrun = CommandListB\n");
        Assert.DoesNotContain("checktextureoverride", ini);
        Assert.DoesNotContain("hash = " + A.Key, ini);
        Assert.Contains("run = CommandListA", Trace(ini, A));
        Assert.DoesNotContain("run = CommandListB", Trace(ini, A));
        Assert.Contains("run = CommandListB", Trace(ini, B));
        Assert.DoesNotContain("run = CommandListA", Trace(ini, B));
        Assert.Empty(Trace(ini, A with { Vb2 = null }));
        Assert.Empty(Trace(ini, A with { Hash = "55667788" }));
        Assert.Empty(Trace(ini, A with { Vb1 = "66778899" }));
        Assert.Empty(Trace(ini, A with { Vb0 = "778899aa" }));
        Assert.Equal(Trace(ini, A), Trace(ini, A));
        Assert.Equal(2, new[] { A, A }.Sum(draw => Trace(ini, draw).Count(line => line == "handling = skip")));
    }

    [Fact]
    public void Draw_shape_metadata_and_capture_before_toggle_stay_on_their_own_side_of_the_guard()
    {
        string ini = DrawSelectorIni.Lower($"[TextureOverride_Target]\nhash = {A.Key}\nmatch_priority = 0\n"
            + "match_first_index = 3\nmatch_index_count = 6\nResource_Posed = ref vb0\n"
            + "if $enabled == 1\nhandling = skip\nendif\npost Resource_Posed = null\n\n[Resource_Other]\ntype = Buffer\n");
        Assert.True(ini.IndexOf("match_index_count = 6", StringComparison.Ordinal)
            < ini.IndexOf("if vb0 ==", StringComparison.Ordinal));
        // the ib is the section's own match: the predicate tests the vertex slots alone, and no tag
        // section is minted for the ib
        Assert.DoesNotContain("ib ==", ini);
        Assert.DoesNotContain($"[TextureOverride_DrawTag_{A.Hash}]", ini);
        Assert.Contains($"[TextureOverride_DrawTag_{A.Vb1}]", ini);
        var trace = Trace(ini, A);
        Assert.Contains("Resource_Posed = ref vb0", trace);
        Assert.DoesNotContain("handling = skip", trace);
        Assert.Empty(Trace(ini, B));
        Assert.Contains("post Resource_Posed = null\nendif\n\n[Resource_Other]", ini);
    }

    /// <summary>A section fires where its constrained slots are bound to its hashes. A mesh with fewer
    /// streams binds nothing in the missing slot, so the fuller section never fires on it, while the barer
    /// mesh's own section — constraining nothing there — fires on every mesh sharing its ib.</summary>
    [Fact]
    public void A_section_fires_on_its_own_draws_and_never_on_a_mesh_missing_a_slot_it_constrains()
    {
        var bare = new DrawSelector(A.Hash);
        var noSkin = A with { Vb2 = null };
        Assert.True(A.FiresOn(A));
        Assert.False(A.FiresOn(B));
        Assert.False(A.FiresOn(noSkin));
        Assert.True(noSkin.FiresOn(A));
        Assert.True(bare.FiresOn(A));
        Assert.False(A.FiresOn(bare));
        Assert.False(bare.FiresOn(bare with { Hash = "55667788" }));
        // overlap is either direction: the two sections can act on one draw
        Assert.True(A.Overlaps(noSkin));
        Assert.True(noSkin.Overlaps(A));
        Assert.False(A.Overlaps(B));
    }

    /// <summary>The emitted selector keeps only the slots that exclude the other selectors on its ib:
    /// nothing when it stands alone, one slot when one differs, both when both are needed, and the full
    /// selector when a rival binds identical streams — no section can tell those two apart.</summary>
    [Fact]
    public void Reduce_keeps_only_the_slots_that_separate_a_mesh_from_the_others_on_its_ib()
    {
        var full = new DrawSelector(A.Hash, null, A.Vb1, A.Vb2);
        Assert.Equal(new DrawSelector(A.Hash), full.Reduce(Array.Empty<DrawSelector>()));
        Assert.Equal(new DrawSelector(A.Hash), full.Reduce(new[] { full, new DrawSelector("55667788", null, A.Vb1, A.Vb2) }));
        Assert.Equal(full with { Vb2 = null }, full.Reduce(new[] { full with { Vb1 = "66778899" } }));
        Assert.Equal(full with { Vb1 = null }, full.Reduce(new[] { full with { Vb2 = "44556677" } }));
        Assert.Equal(full, full.Reduce(new[] { full with { Vb1 = "66778899" }, full with { Vb2 = "44556677" } }));
        // a rival identical on every bound stream cannot be excluded: the full selector stands, ambiguous
        Assert.Equal(full, full.Reduce(new[] { full with { Vb0 = "778899aa" } }));
        // a barer rival is excluded by any one slot it lacks; the barer mesh itself can exclude nothing
        Assert.Equal(full with { Vb2 = null }, full.Reduce(new[] { new DrawSelector(A.Hash) }));
        Assert.Equal(new DrawSelector(A.Hash), new DrawSelector(A.Hash).Reduce(new[] { full }));
    }

    /// <summary>The roster answers by the same rule: a key reaches exactly the meshes its section fires on. A
    /// wearer whose mesh lacks stream 2 is out of reach of a section constraining it, so that section stays
    /// private to its own outfit; the barer mesh's section reaches the fuller one.</summary>
    [Fact]
    public void Sharing_and_presence_answer_by_what_a_section_fires_on()
    {
        var wearers = new[] { new SharingIndex.Wearer("A", null, "A1", null),
            new SharingIndex.Wearer("B", null, "B1", null) };
        SharingIndex Index(string other) => SharingIndex.FromMeasurements("test", wearers,
            new Dictionary<string, int[]>(), new Dictionary<string, int[]> { [A.Key] = new[] { 0 }, [other] = new[] { 1 } },
            new Dictionary<int, string[]> { [0] = new[] { A.Key }, [1] = new[] { other } });
        var distinct = Index(B.Key);
        Assert.Empty(distinct.MeshOtherWearers(A.Key, "A", "A1"));
        Assert.Single(distinct.MeshOtherWearers(A.Hash, "A", "A1"));
        Assert.Equal(A.Key, Assert.Single(distinct.WitnessIbs("A", "A1")));
        Assert.Equal(new[] { A, B }, distinct.SelectorsOn(A.Hash).OrderBy(s => s.Key, StringComparer.Ordinal));
        string noSkin = (A with { Vb2 = null }).Key;
        var barer = Index(noSkin);
        Assert.Empty(barer.MeshOtherWearers(A.Key, "A", "A1"));
        Assert.Equal(A.Key, Assert.Single(barer.WitnessIbs("A", "A1")));
        Assert.Single(barer.MeshOtherWearers(noSkin, "B", "B1"));
        Assert.Empty(barer.WitnessIbs("B", "B1"));
        Assert.True(DrawSelector.Parse(A.Key).Overlaps(A));
    }

    /// <summary>An emitted key is a section, not a draw: a slot it dropped is unconstrained, not unbound. Read
    /// two emitted keys as draws and a fuller/barer pair sharing a slot the fuller key dropped misses its
    /// collision, though the barer mesh's section does fire on the fuller mesh. The plan-time checks judge
    /// each section against the OTHER mesh's full selector, which is what the build supplies.</summary>
    [Fact]
    public void Collision_checks_judge_emitted_keys_against_full_selectors()
    {
        var fullX = new DrawSelector(A.Hash, null, A.Vb1, A.Vb2);
        var fullY = new DrawSelector(A.Hash, null, A.Vb1, null);
        var emittedX = fullX.Reduce(new[] { fullY });
        var emittedY = fullY.Reduce(new[] { fullX });
        Assert.Equal(fullX with { Vb1 = null }, emittedX);
        Assert.Equal(fullY, emittedY);
        Assert.False(emittedX.Overlaps(emittedY));
        Assert.True(emittedY.FiresOn(fullX));
        var fulls = new Dictionary<string, DrawSelector> { [emittedX.Key] = fullX, [emittedY.Key] = fullY };
        bool Collide(string a, string b) =>
            DrawSelector.Parse(a).FiresOn(fulls[b]) || DrawSelector.Parse(b).FiresOn(fulls[a]);
        var gate = BuildEmissionGate.Unconditional;
        Assert.NotNull(ModBuilder.ReplacedMeshConflict(
            new[] { ("X", emittedX.Key, gate), ("Y", emittedY.Key, gate) }, Collide));
        // and two meshes a slot really separates stay apart under the same judgment
        var fullZ = fullX with { Vb2 = "44556677" };
        fulls[fullZ.Reduce(new[] { fullX }).Key] = fullZ;
        Assert.Null(ModBuilder.ReplacedMeshConflict(
            new[] { ("X", fullX.Reduce(new[] { fullZ }).Key, gate), ("Z", fullZ.Reduce(new[] { fullX }).Key, gate) }, Collide));
    }

    /// <summary>A vertex-slot tag colliding with a texture tag is refused where the rows are known, naming
    /// both sides in the change list's own words.</summary>
    [Fact]
    public void A_buffer_tag_colliding_with_a_texture_tag_names_both_rows()
    {
        // RetexTag(h) = 1,000,000 + h mod 15,000,000: these two hashes derive one tag
        const string texture = "00000001", buffer = "00e4e1c1";
        Assert.Equal(MigotoEmitter.RetexTag(texture), MigotoEmitter.RetexTag(buffer));
        var ex = Assert.Throws<AuthoredRefusalException>(() => MigotoEmitter.RefuseTagCollisions(
            Array.Empty<(string, string)>(), new[] { (texture, "cloth1") },
            bufferTags: new[] { (buffer, "c_vesna01_body_lod0") }));
        Assert.Contains("the mesh c_vesna01_body_lod0", ex.Message);
        Assert.Contains("stock texture 00000001 on cloth1", ex.Message);
        Assert.Contains("Leave the edit on c_vesna01_body_lod0 or the new textures on cloth1 out of the build.", ex.Message);
        // no collision, no refusal — and a buffer against itself is one resource, not two
        MigotoEmitter.RefuseTagCollisions(Array.Empty<(string, string)>(), new[] { ("00000002", "cloth1") },
            bufferTags: new[] { (buffer, "body"), (buffer, "body") });
    }

    [Fact]
    public void Buffer_tags_share_existing_values_and_refuse_instead_of_aliasing_a_different_hash()
    {
        string target = $"[TextureOverride_Target]\nhash = {A.Key}\nmatch_priority = 0\nhandling = skip\n";
        string tag = $"\n[TextureOverride_Texture]\nhash = {A.Vb2}\nfilter_index = {MigotoEmitter.RetexTag(A.Vb2!)}\nmatch_priority = 100\n";
        string ini = DrawSelectorIni.Lower(target + tag);
        Assert.Single(Regex.Matches(ini, "hash = " + A.Vb2 + "\\n"));
        Assert.Contains("handling = skip", Trace(ini, A));
        string collision = $"\n[TextureOverride_Texture]\nhash = ffffffff\nfilter_index = {MigotoEmitter.RetexTag(A.Vb2!)}\nmatch_priority = 100\n";
        Assert.ThrowsAny<InvalidOperationException>(() => DrawSelectorIni.Lower(target + collision));
    }

    [Fact]
    public void Replacement_claims_distinguish_skin_bytes_but_refuse_missing_evidence()
    {
        var gate = BuildEmissionGate.Unconditional;
        Assert.Null(ModBuilder.ReplacedMeshConflict(new[] { ("A", A.Key, gate), ("B", B.Key, gate) }));
        Assert.NotNull(ModBuilder.ReplacedMeshConflict(new[]
            { ("A", A.Key, gate), ("B", (B with { Vb2 = null }).Key, gate) }));
        Assert.NotNull(ModBuilder.ReplacedMeshConflict(new[] { ("A", A.Key, gate), ("B", A.Key, gate) }));
    }

    // Executes the emitted predicates against resource hashes; tags come from the emitted sections.
    static List<string> Trace(string ini, DrawSelector draw)
    {
        var sections = Regex.Split(ini, @"(?m)(?=^\[)").Where(s => s.StartsWith("[TextureOverride_", StringComparison.Ordinal)).ToArray();
        string? Value(string section, string field) => Regex.Match(section, @"(?m)^" + field + @" = ([^\n]+)").Groups[1].Value is { Length: > 0 } value ? value : null;
        var tags = sections.Where(s => Value(s, "filter_index") is not null)
            .ToDictionary(s => Value(s, "hash")!, s => int.Parse(Value(s, "filter_index")!));
        var bound = draw.Bindings().ToDictionary(b => b.Slot, b => tags.GetValueOrDefault(b.Hash));
        var trace = new List<string>();
        foreach (string section in sections.Where(s => Value(s, "hash") == draw.Hash))
        {
            var stack = new Stack<bool>();
            bool active = true;
            foreach (string line in section.Split('\n').Skip(1))
            {
                if (line.StartsWith("if ", StringComparison.Ordinal))
                {
                    stack.Push(active);
                    active &= line[3..].Split(" && ").All(term =>
                    {
                        var sides = term.Split(" == ");
                        return bound.GetValueOrDefault(sides[0]) == int.Parse(sides[1]);
                    });
                }
                else if (line == "endif") active = stack.Pop();
                else if (active && (line.StartsWith("run = ") || line.StartsWith("Resource_")
                    || line.StartsWith("$seen") || line == "handling = skip")) trace.Add(line);
            }
            Assert.Empty(stack);
        }
        return trace;
    }
}
