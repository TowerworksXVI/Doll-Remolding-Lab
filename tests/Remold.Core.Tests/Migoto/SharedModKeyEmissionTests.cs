using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>Binding a change to the mod's OWN key is supported, and the change list says so — the two
/// switch together. One key is one variable, so the mod gate and the change gate have to name the same
/// position in it: a mod gate asking for one value inside a change gate asking for another is a block no
/// press can ever open.</summary>
public sealed class SharedModKeyEmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "gf2-sharedmodkey-" + Guid.NewGuid().ToString("N"));

    public SharedModKeyEmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void A_hide_on_the_mods_own_key_gates_once_and_still_draws()
    {
        string ini = Hide(modKey: "F6", hideKey: "F6");

        Assert.Contains("hash = dddd4444\nmatch_priority = 0\nif $zz_key_f6 == 0\nhandling = skip\nendif\n",
            ini);
        Assert.DoesNotContain("if $zz_key_f6 == 0\nif $zz_key_f6 ==", ini);
    }

    [Fact]
    public void A_hide_on_another_key_still_nests_two_gates()
    {
        string ini = Hide(modKey: "F6", hideKey: "F9");

        Assert.Contains("hash = dddd4444\nmatch_priority = 0\nif $zz_key_f6 == 0\nif $zz_key_f9 == 0\n"
            + "handling = skip\nendif\nendif\n", ini);
    }

    [Fact]
    public void A_retexture_on_the_mods_own_key_gates_once_and_still_binds()
    {
        string source = Path.Combine(_root, "src");
        string outDir = Path.Combine(_root, "retex");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outDir);
        string dds = Path.Combine(source, "img.dds");
        FlatDds.Write(dds, (10, 20, 30, 255));

        new MigotoEmitter().BuildOverlaysOnly(outDir,
            new[] { new RetexEntry("part_base_cccc7777", "cccc7777", dds, new KeyRef("F6", 0)) },
            hideHashes: null, modKey: "F6");

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        Assert.Contains("hash = cccc7777\nmatch_priority = 0\nif $zz_key_f6 == 0\nthis = ", ini);
        Assert.DoesNotContain("if $zz_key_f6 == 0\nif $zz_key_f6 ==", ini);
    }

    /// <summary>The invariant behind all three, stated once over every emission this suite pins: inside one
    /// section, a variable already held open at one value is never opened again at another. That block
    /// cannot be reached however the keys are pressed, so an emission holding one ships a change that never
    /// draws. Every committed golden is swept, and so are the built emissions below — the shapes no
    /// committed golden happens to contain.
    ///
    /// <para>A game-wide retexture section carrying SEVERAL gated rebinds is one of them, and it is the
    /// shape this invariant was silently broken in: one hash owns one section, so alternate answers of one
    /// part stack their rebinds inside it and each brings a key term of its own. Swept here at both
    /// tiers — a claim on a key of its own, and a claim on the mod's own key. What ModBuilder refuses
    /// before the emitter ever sees it is pinned at that refusal instead
    /// (<c>ModBuilderTests.A_change_at_the_mod_keys_off_position_refuses_by_name</c>); this sweep asks the
    /// narrower question, that the shapes it DOES emit hold no contradiction.</para></summary>
    [Fact]
    public void No_emission_nests_one_variable_at_two_values()
    {
        var goldens = Directory.GetFiles(GoldenDir(), "*.ini");
        Assert.NotEmpty(goldens);
        var emissions = goldens.Select(path => (Path.GetFileName(path), File.ReadAllText(path)))
            .Append(("a hide sharing the mod's key", Hide(modKey: "F6", hideKey: "F6")))
            .Append(("a hide on its own key", Hide(modKey: "F6", hideKey: "F9")))
            .Append(("two alternate images on a key of their own",
                MultiImageRetex(modKey: "F6", claimKey: "F9")))
            .Append(("two images one of which is on the mod's own key",
                MultiImageRetex(modKey: "F6", claimKey: "F6")));
        foreach (var (name, ini) in emissions)
            foreach (var (section, body) in Sections(ini))
                Assert.True(Contradiction(body) is null, $"{name} {section}: {Contradiction(body)}");
    }

    /// <summary>One GAME-WIDE retexture section carrying two gated rebinds of one stock hash: the first
    /// claim at position 0 of <paramref name="claimKey"/>, the second at position 0 of a key of its own.
    /// Both positions are ones ModBuilder lets through — a claim past position 0 of the mod's own key never
    /// reaches the emitter — so what this builds is emission the production route can really produce.
    /// </summary>
    private string MultiImageRetex(string modKey, string claimKey)
    {
        string source = Path.Combine(_root, $"multi-{modKey}-{claimKey}-src");
        string outDir = Path.Combine(_root, $"multi-{modKey}-{claimKey}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outDir);
        string first = Path.Combine(source, "first.dds");
        string second = Path.Combine(source, "second.dds");
        FlatDds.Write(first, (10, 20, 30, 255));
        FlatDds.Write(second, (40, 50, 60, 255));

        new MigotoEmitter().BuildOverlaysOnly(outDir,
            new[]
            {
                new RetexEntry("part_base_cccc7777", "cccc7777", new[]
                {
                    new RetexImage(first, new KeyRef(claimKey, 0)),
                    new RetexImage(second, new KeyRef("F10", 0)),
                }),
            },
            hideHashes: null, modKey: modKey);

        return File.ReadAllText(Path.Combine(outDir, "mod.ini"));
    }

    /// <summary>The first variable this section holds open at two different values, or null when it holds
    /// none. Only NESTING contradicts: two gates side by side on one variable are alternatives, and each
    /// opens on its own press.</summary>
    private static string? Contradiction(IReadOnlyList<string> body)
    {
        var open = new List<(string Var, string State)>();
        foreach (string line in body)
        {
            if (line == "endif")
            {
                if (open.Count > 0) open.RemoveAt(open.Count - 1);
                continue;
            }
            if (!line.StartsWith("if $", StringComparison.Ordinal)) continue;
            var parts = line.Split(' ');
            if (parts.Length != 4 || parts[2] != "==") continue;
            var term = (Var: parts[1], State: parts[3]);
            var held = open.FirstOrDefault(other => other.Var == term.Var);
            if (held.Var is not null && held.State != term.State)
                return $"{term.Var} is held at {held.State} and opened again at {term.State}";
            open.Add(term);
        }
        return null;
    }

    private static IEnumerable<(string Section, IReadOnlyList<string> Body)> Sections(string ini)
    {
        string name = "(preamble)";
        var body = new List<string>();
        foreach (string raw in ini.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                yield return (name, body);
                name = line;
                body = new List<string>();
                continue;
            }
            body.Add(line);
        }
        yield return (name, body);
    }

    private string Hide(string modKey, string hideKey)
    {
        string outDir = Path.Combine(_root, $"hide-{modKey}-{hideKey}");
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null,
            hideHashes: new[] { "dddd4444" }, modKey: modKey,
            hideKeys: new Dictionary<string, IReadOnlyList<KeyRef>>
            {
                ["dddd4444"] = new KeyRef[] { hideKey },
            });
        return File.ReadAllText(Path.Combine(outDir, "mod.ini"));
    }

    private static string GoldenDir([System.Runtime.CompilerServices.CallerFilePath] string self = "") =>
        Path.Combine(Path.GetDirectoryName(self)!, "golden");
}
