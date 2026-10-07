using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>One hide section answering several changes. Each claim keeps its own key positions, presence
/// latch and twin verdict, and the section skips while any claim asks; what one claim already covers,
/// another adds nothing to.</summary>
public sealed class HideClaimEmissionTests : IDisposable
{
    private const string Hash = "aaaa1111";

    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "gf2-hideclaims-" + Guid.NewGuid().ToString("N"));

    public HideClaimEmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void A_hide_in_every_state_covers_a_keyed_claim_on_the_same_draw()
    {
        string section = Hide(new HideClaim(new KeyRef[] { "F9" }), new HideClaim(Array.Empty<KeyRef>()));

        Assert.EndsWith("if $zz_key_f6 == 0\nhandling = skip\nendif", section);
        Assert.Equal(1, Skips(section));
    }

    [Fact]
    public void Two_claims_on_the_same_key_position_skip_once()
    {
        string section = Hide(new HideClaim(new KeyRef[] { "F9" }), new HideClaim(new KeyRef[] { "F9" }));

        Assert.EndsWith("if $zz_key_f6 == 0\nif $zz_key_f9 == 0\nhandling = skip\nendif\nendif", section);
        Assert.Equal(1, Skips(section));
    }

    [Fact]
    public void Claims_on_different_keys_each_skip_under_their_own()
    {
        string section = Hide(new HideClaim(new KeyRef[] { "F8" }), new HideClaim(new KeyRef[] { "F9" }));

        Assert.EndsWith("if $zz_key_f6 == 0\nif $zz_key_f8 == 0\nhandling = skip\nendif\nendif\n"
            + "if $zz_key_f6 == 0\nif $zz_key_f9 == 0\nhandling = skip\nendif\nendif", section);
    }

    /// <summary>Two claims that between them cover every position of a declared two-position key say what
    /// one bare skip says, exactly as one claim covering both always has.</summary>
    [Fact]
    public void Claims_covering_every_position_of_a_key_between_them_collapse_to_one_skip()
    {
        string section = Hide(new[] { new KeyCycle("F9", 2, 0) },
            new HideClaim(new[] { new KeyRef("F9", 0) }), new HideClaim(new[] { new KeyRef("F9", 1) }));

        Assert.EndsWith("if $zz_key_f6 == 0\nhandling = skip\nendif", section);
        Assert.Equal(1, Skips(section));
    }

    [Fact]
    public void Each_claim_waits_on_its_own_presence_latch()
    {
        string section = Hide(new HideClaim(new KeyRef[] { "F8" }, Latch: "one"),
            new HideClaim(new KeyRef[] { "F9" }, Latch: "two"));

        Assert.EndsWith("if $zz_key_f6 == 0\nif $zz_key_f8 == 0\nif $zz_gate_one == 1\nhandling = skip\n"
            + "endif\nendif\nendif\n"
            + "if $zz_key_f6 == 0\nif $zz_key_f9 == 0\nif $zz_gate_two == 1\nhandling = skip\n"
            + "endif\nendif\nendif", section);
    }

    /// <summary>Claims on one mesh of a guarded signature open the guard once around their skips, which is
    /// the section a single claim has always emitted.</summary>
    [Fact]
    public void Claims_on_one_twin_open_its_guard_once()
    {
        string section = HideGuarded(new[] { 1 },
            new HideClaim(new KeyRef[] { "F8" }, Verdict: 1), new HideClaim(new KeyRef[] { "F9" }, Verdict: 1));

        Assert.EndsWith("if $zz_tw_aaaa1111 == 1\n"
            + "if $zz_key_f6 == 0\nif $zz_key_f8 == 0\nhandling = skip\nendif\nendif\n"
            + "if $zz_key_f6 == 0\nif $zz_key_f9 == 0\nhandling = skip\nendif\nendif\n"
            + "endif", section);
    }

    /// <summary>A claim on a twin the probe identifies skips at that twin's draws only; a claim on a twin it
    /// cannot identify skips at every draw of the signature, which is what its build warning says.</summary>
    [Fact]
    public void A_claim_with_no_verdict_beside_one_with_a_verdict_skips_at_every_draw()
    {
        string section = HideGuarded(new[] { 1 },
            new HideClaim(new KeyRef[] { "F8" }, Verdict: 1), new HideClaim(new KeyRef[] { "F9" }));

        Assert.Contains("$zz_t = ps-t0\n", section);
        Assert.EndsWith("if $zz_key_f6 == 0\nif $zz_key_f8 == 0\nif $zz_tw_aaaa1111 == 1\nhandling = skip\n"
            + "endif\nendif\nendif\n"
            + "if $zz_key_f6 == 0\nif $zz_key_f9 == 0\nhandling = skip\nendif\nendif", section);
    }

    [Fact]
    public void Claims_on_two_twins_each_skip_at_their_own_verdict()
    {
        string section = HideGuarded(new[] { 1, 2 },
            new HideClaim(new KeyRef[] { "F8" }, Verdict: 1), new HideClaim(new KeyRef[] { "F9" }, Verdict: 2));

        Assert.EndsWith("if $zz_key_f6 == 0\nif $zz_key_f8 == 0\nif $zz_tw_aaaa1111 == 1\nhandling = skip\n"
            + "endif\nendif\nendif\n"
            + "if $zz_key_f6 == 0\nif $zz_key_f9 == 0\nif $zz_tw_aaaa1111 == 2\nhandling = skip\n"
            + "endif\nendif\nendif", section);
        Assert.DoesNotContain("zz_twok", File.ReadAllText(Path.Combine(_root, "guarded", "mod.ini")));
    }

    [Fact]
    public void A_claim_on_a_verdict_its_guard_does_not_admit_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => HideGuarded(new[] { 1 },
            new HideClaim(new KeyRef[] { "F8" }, Verdict: 2)));

        Assert.Contains("twin verdict 2", ex.Message);
    }

    private string Hide(params HideClaim[] claims) => Hide(null, claims);

    private string Hide(IReadOnlyList<KeyCycle>? cycles, params HideClaim[] claims)
    {
        string outDir = Path.Combine(_root, "plain");
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null,
            hideHashes: new[] { Hash }, modKey: "F6",
            hideClaims: new Dictionary<string, IReadOnlyList<HideClaim>> { [Hash] = claims },
            keyCycles: cycles);
        return Section(File.ReadAllText(Path.Combine(outDir, "mod.ini")));
    }

    private string HideGuarded(int[] ownVerdicts, params HideClaim[] claims)
    {
        string outDir = Path.Combine(_root, "guarded");
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null,
            hideHashes: new[] { Hash }, modKey: "F6",
            hideClaims: new Dictionary<string, IReadOnlyList<HideClaim>> { [Hash] = claims },
            twinGuards: new[]
            {
                new TwinGuard(Hash, MigotoEmitter.TwinVar(Hash), ownVerdicts, new[]
                {
                    new TwinProbeTag("11bb22cc", MigotoEmitter.RetexTag("11bb22cc"), 1),
                    new TwinProbeTag("22bb33cc", MigotoEmitter.RetexTag("22bb33cc"), 2),
                }),
            });
        return Section(File.ReadAllText(Path.Combine(outDir, "mod.ini")));
    }

    /// <summary>The hide section's body, from its hash line to the blank line that ends it.</summary>
    private static string Section(string ini)
    {
        int at = ini.IndexOf($"hash = {Hash}\nmatch_priority = 0\n", StringComparison.Ordinal);
        Assert.True(at >= 0, "no hide section");
        int end = ini.IndexOf("\n\n", at, StringComparison.Ordinal);
        return end < 0 ? ini[at..].TrimEnd('\n') : ini[at..end];
    }

    private static int Skips(string section) =>
        section.Split('\n').Count(line => line == "handling = skip");
}
