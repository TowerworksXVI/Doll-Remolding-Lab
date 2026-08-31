using System;
using System.IO;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// What an install with no readable shader slot catalog still emits. The slot probe is what every
/// slot-aware section is built around — a twin guard's identification, a scoped retexture's bind — and all
/// of them shipped before the measurement existed, over the classic register range. So an unreadable
/// catalog costs the ramp and the registers past the classic range, and costs nothing else.
/// </summary>
public class ShaderSlotFallbackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-slotfloor-" + Guid.NewGuid().ToString("N"));

    public ShaderSlotFallbackTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static MigotoEmitter Floor() => new() { Slots = ShaderSlotPlan.StockFloor };

    private string Dds(string name)
    {
        string p = Path.Combine(_root, name);
        FlatDds.Write(p, (90, 90, 90, 255));
        return p;
    }

    /// <summary>A guard's sticky variable is written by the probe and by nothing else, so a probe that
    /// sweeps no register leaves the variable at 0 and every line inside the guard unreachable — the
    /// guarded mesh swap or hide would silently draw vanilla.</summary>
    [Fact]
    public void A_twin_guard_under_the_floor_still_has_a_writer_for_its_variable()
    {
        string hash = "3ff9db6d";
        string outDir = Path.Combine(_root, "guard");
        Floor().BuildOverlaysOnly(outDir, entries: null,
            hideHashes: new[] { "aaaa1111" },
            twinGuards: new[]
            {
                new TwinGuard("aaaa1111", "zz_tw_aaaa1111", new[] { 1 },
                    new[] { new TwinProbeTag(hash, MigotoEmitter.RetexTag(hash), 1) }),
            });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        foreach (int s in ShaderSlotPlan.StockFloor.StockMaps)
            Assert.Contains($"$zz_t = ps-t{s}\n", ini);
        Assert.Contains($"if $zz_t == {MigotoEmitter.RetexTag(hash)}\n$zz_tw_aaaa1111 = 1\nendif\n", ini);
        // the verdict test, and the skip it guards, are reachable because the write above can happen
        Assert.Contains("if $zz_tw_aaaa1111 == 1\nhandling = skip\n", ini);
        // the registers the measurement added are the ones given up
        Assert.DoesNotContain("ps-t7", ini);
        Assert.DoesNotContain("ps-t8", ini);
    }

    /// <summary>A scoped retexture binds at whichever register the probe answered on. With no probe the
    /// section carries no bind at all and the edit never shows.</summary>
    [Fact]
    public void A_scoped_retexture_under_the_floor_still_probes_and_binds()
    {
        string outDir = Path.Combine(_root, "scoped");
        Floor().BuildOverlaysOnly(outDir, entries: null,
            scopedEntries: new[]
            {
                new ScopedRetexEntry("face_a", "ffff0000", new[]
                {
                    new ScopedRetexImage(Dds("one.dds"), new[] { new ScopedAnchor("aaaa1111", "one") }),
                }),
            });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        foreach (int s in ShaderSlotPlan.StockFloor.StockMaps)
        {
            Assert.Contains($"Resource_RtxSave{s} = ref ps-t{s}\n", ini);
            Assert.Contains($"if $zz_rt == {MigotoEmitter.RetexTag("ffff0000")}\n$zz_rslot = {s}\nendif\n", ini);
            Assert.Contains($"if $zz_rslot == {s}\nps-t{s} = Resource_Rtx0\nendif\n", ini);
            Assert.Contains($"post ps-t{s} = Resource_RtxSave{s}\n", ini);
        }
    }

    /// <summary>The floor is a floor, not a ceiling: a build that CAN read the measurement probes whatever
    /// it states, which reaches further.</summary>
    [Fact]
    public void The_measured_catalog_reaches_past_the_floor()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, ShaderSlotPlan.StockFloor.StockMaps);
        Assert.Empty(ShaderSlotPlan.StockFloor.Ramp);
        Assert.True(ShaderSlotPlan.Shipped.StockMaps.Count > ShaderSlotPlan.StockFloor.StockMaps.Count);
        Assert.NotEmpty(ShaderSlotPlan.Shipped.Ramp);
    }
}
