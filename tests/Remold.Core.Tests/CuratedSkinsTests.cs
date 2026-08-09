using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Migoto;
using Remold.Core.Model;
using Remold.Core.Tables;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The curated whitelist: skins the design DB does not enumerate, the two routes that reach their
/// prefabs, the merge that keeps a half-described character from listing twice, and the shared-face build
/// warning.
///
/// <para>Table-shape tests assert the REAL curated values — it is functional data, and a test written
/// against invented names would pass while the shipped table pointed somewhere else (the same rule
/// <see cref="TableRosterTests"/> follows for the curated summon prefixes). The synthetic corpus that
/// exercises the RESOLUTION mechanics uses invented names throughout, because nothing there depends on
/// which subject it is.</para>
/// </summary>
public class CuratedSkinsTests
{
    // ---- the table itself -------------------------------------------------------------------------

    [Fact]
    public void Table_GroupsIntoThreeCharacters_WithTheirExactLabels()
    {
        var byCharacter = CuratedSkins.All
            .GroupBy(e => e.Character, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        Assert.Equal(3, byCharacter.Count);
        Assert.Equal(9, CuratedSkins.All.Count);

        // display strings are curated and exact — the whole point of the feature
        Assert.All(byCharacter["Mayling"], e => Assert.Equal("Mayling", e.CharacterDisplay));
        // two SHIPPED BUILDS of this character, each pickable: same mesh names, different assets
        Assert.Equal(new[] { "Barracks", "Crew Deck" },
            byCharacter["Mayling"].Select(e => e.OutfitDisplay).ToArray());

        Assert.All(byCharacter["CommanderMale"], e => Assert.Equal("Commander (M)", e.CharacterDisplay));
        Assert.Equal(new[] { "001", "002", "003", "Neutral" },
            byCharacter["CommanderMale"].Select(e => e.OutfitDisplay).ToArray());

        Assert.All(byCharacter["CommanderFemale"], e => Assert.Equal("Commander (F)", e.CharacterDisplay));
        Assert.Equal(new[] { "001", "002", "003" },
            byCharacter["CommanderFemale"].Select(e => e.OutfitDisplay).ToArray());
    }

    [Fact]
    public void Table_IdsAreNegativeAndUnique_SoNoDbRowCanCollideWithThem()
    {
        // the snapshot is keyed on ModelConfigId and every id the design DB issues is positive, so the
        // sign — not a lucky choice of number — is what keeps a curated part list off a real outfit
        Assert.All(CuratedSkins.All, e => Assert.True(e.ModelConfigId < 0, $"'{e.Stem}' must carry a negative id"));
        Assert.Equal(CuratedSkins.All.Count, CuratedSkins.All.Select(e => e.ModelConfigId).Distinct().Count());
        Assert.Equal(CuratedSkins.All.Count, CuratedSkins.All.Select(e => e.Stem).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Table_EveryRoutedEntryCarriesExactlyOneShape_AndAlwaysNamesItsRoot()
    {
        foreach (var e in CuratedSkins.All)
        {
            if (e.Route is not { } route) continue;   // the stem formula reaches this one; only the row is curated
            // the root is named on BOTH shapes — a bundle can hold sibling roots whichever way it was
            // reached, and the root IS the stem, which is what makes the prefab's own slots this
            // subject's under the workbench ownership rule
            Assert.Equal(e.Stem, route.RootName);
            Assert.True(route.Address is null ^ route.Bundle is null, $"'{e.Stem}' must carry exactly one route shape");
            if (route.Address is not null)
            {
                // a catalog asset path to a prefab. NOT necessarily under ConfigPrefab: a shipped build can
                // be addressed straight out of the art tree, and that address is as real as the other kind
                Assert.StartsWith("Assets/", route.Address, StringComparison.Ordinal);
                Assert.EndsWith(".prefab", route.Address, StringComparison.Ordinal);
                Assert.Empty(route.ExtraBundles);   // its catalog row carries a real dependency closure
                continue;
            }
            // LOGICAL bundle ids, the catalog's own identity — a bare hash resolves nothing
            Assert.EndsWith(".bundle", route.Bundle!, StringComparison.Ordinal);
            // a bare bundle has no closure, so its material/texture bundles are named outright
            Assert.NotEmpty(route.ExtraBundles);
            Assert.All(route.ExtraBundles, b => Assert.EndsWith(".bundle", b, StringComparison.Ordinal));
            Assert.DoesNotContain(route.Bundle, route.ExtraBundles);
            Assert.Equal(route.ExtraBundles.Count, route.ExtraBundles.Distinct(StringComparer.Ordinal).Count());
        }
        // both shapes, and the routeless row, are actually present in the shipped table
        Assert.Contains(CuratedSkins.All, e => e.Route?.Address is not null);
        Assert.Contains(CuratedSkins.All, e => e.Route?.Bundle is not null);
        Assert.Contains(CuratedSkins.All, e => e.Route is null);
    }

    [Fact]
    public void Table_EntriesBecomeBareLabelledOutfits_CarryingPrefixAndRoute()
    {
        var neutral = CuratedSkins.All.Single(e => e.OutfitDisplay == "Neutral");
        var outfit = neutral.ToOutfit();

        // Other is the kind whose label renders BARE, so "Neutral" is the whole label
        Assert.Equal(OutfitKind.Other, outfit.Kind);
        Assert.Equal("Neutral", FriendlyNames.KindAndLabel(outfit));
        // it shares skin 001's mesh family, so the prefix is the un-numbered one
        Assert.Equal("c_CommanderMale_dorm_", outfit.MeshPrefix);
        // this one's prefab sits under a context root of the stem-address formula, so it needs no route
        Assert.Null(neutral.Route);
        Assert.Null(outfit.Route);

        // a routed entry hands its route straight through
        var routed = CuratedSkins.All.Single(e => e.Stem == "CommanderMale");
        Assert.Same(routed.Route, routed.ToOutfit().Route);
    }

    [Fact]
    public void Table_ListsBothMaylingBuilds_EachAtItsOwnAddress()
    {
        // The builds ship the same slot NAMES and different meshes, so the entry that separates them is the
        // address. Pointing two at one address, or either at the other's, silently hands a picker the model
        // the game does not draw where it says it does. Bundles can ship sibling roots, which is where a
        // shared address would be easiest to write and hardest to notice.
        var builds = CuratedSkins.All.Where(e => e.Character == "Mayling").ToList();

        Assert.Equal(2, builds.Count);
        Assert.All(builds, e => Assert.Equal("c_Mayling_dorm_", e.MeshPrefix));   // one mesh family, two assets
        Assert.Equal("Assets/ConfigPrefab/BarrackModel/Character/Mayling/Mayling_dorm.prefab",
            builds[0].Route!.Address);
        Assert.Equal("Assets/ArtsResource/Lobby_NPC/Mayling/Models/c_Mayling_dorm_nobag_skin_model.prefab",
            builds[1].Route!.Address);
        Assert.Equal(2, builds.Select(e => e.Route!.Address).Distinct(StringComparer.Ordinal).Count());
        // the shared prefix means no build's root can be inferred from another's
        Assert.Equal(builds.Select(e => e.Stem).ToArray(), builds.Select(e => e.Route!.RootName).ToArray());
    }

    [Theory]
    [InlineData("", "Root")]
    [InlineData("only.bundle", "")]
    public void Route_RefusesAHalfSpecifiedBundleRoute(string bundle, string rootName) =>
        Assert.Throws<ArgumentException>(() => SubjectRoute.DirectBundle(bundle, rootName));

    [Theory]
    [InlineData("", "Root")]
    [InlineData("Assets/ConfigPrefab/A/B/B.prefab", "")]
    public void Route_RefusesAHalfSpecifiedAddressRoute(string address, string rootName) =>
        Assert.Throws<ArgumentException>(() => SubjectRoute.Addressable(address, rootName));

    // ---- the merge (the collision question) -------------------------------------------------------

    private static Character Db(string name, string? display, params string[] stems) =>
        new(CharId: 1, Name: name, Family: "", GunId: 7, DormModelConfigId: 0,
            Outfits: stems.Select((s, i) => new Outfit(100 + i, s, OutfitKind.Base)).ToList())
        { DisplayName = display };

    [Fact]
    public void MergeInto_AddsEachCuratedCharacter_WhenTheRosterCarriesNone()
    {
        var merged = CuratedSkins.MergeInto(new[] { Db("Wren", "Wren", "WrenSSR01") });

        Assert.Equal(4, merged.Count);
        var male = merged.Single(c => c.Name == "CommanderMale");
        Assert.Equal("Commander (M)", male.DisplayName);
        Assert.Equal(4, male.Outfits.Count);
        Assert.Equal(new[] { "001", "002", "003", "Neutral" },
            male.Outfits.Select(FriendlyNames.Label).ToArray());
        // three carry their own route; the fourth is reached by the stem formula
        Assert.Equal(3, male.Outfits.Count(o => o.Route is not null));
        // the untouched DB character survives whole
        Assert.Single(merged.Single(c => c.Name == "Wren").Outfits);
    }

    [Fact]
    public void MergeInto_FoldsIntoAnExistingCharacterKey_RatherThanListingItTwice()
    {
        // The shipped case: the design DB names this character but resolves NO model rows for her, so she
        // reaches the roster with zero outfits. Merging gives that one row the curated skin instead of
        // standing a second row beside it.
        var merged = CuratedSkins.MergeInto(new[] { Db("Mayling", "Mayling") });

        Assert.Single(merged, c => string.Equals(c.Name, "Mayling", StringComparison.OrdinalIgnoreCase));
        var m = merged.Single(c => c.Name == "Mayling");
        Assert.Equal("Mayling", m.DisplayName);        // the game's own name still labels her
        Assert.Equal(7, m.GunId);                      // and she keeps her DB identity
        Assert.Equal(new[] { "Barracks", "Crew Deck" },
            m.Outfits.Select(FriendlyNames.Label).ToArray());
        Assert.All(m.Outfits, o => Assert.NotNull(o.Route));
    }

    [Fact]
    public void MergeInto_OneDbRowAndSeveralCuratedBuilds_StaysOneCharacterWithEveryBuildUnderIt()
    {
        // The SHIPPED shape, and the one the tree renders wrong when anything upstream loses an outfit: the
        // DB row names this character and resolves no model of its own, the curated table supplies every
        // build, and the result must be ONE character keyed on the internal name whose outfits are all of
        // them. Collapsed to a single outfit the tree shows a leaf labelled off the stem instead of a
        // character with its builds under it, which is what a dropped outfit looks like from the outside.
        var merged = CuratedSkins.MergeInto(new[] { Db("Mayling", "Mayling") });

        var m = Assert.Single(merged, c => string.Equals(c.Name, "Mayling", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Mayling", FriendlyNames.Label(m));                  // the character label, never a stem
        Assert.Equal(2, m.Outfits.Count);
        Assert.Equal(new[] { "Mayling_dorm", "c_Mayling_dorm_nobag_skin_model" },
            m.Outfits.Select(o => o.Stem).ToArray());
        // each build keeps its own identity: distinct ids the snapshot keys on, distinct addresses
        Assert.Equal(2, m.Outfits.Select(o => o.ModelConfigId).Distinct().Count());
        Assert.Equal(2, m.Outfits.Select(o => o.Route!.Address).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void MergeInto_AppendsToTheDbOutfits_WithoutDisplacingThem()
    {
        var merged = CuratedSkins.MergeInto(new[] { Db("Mayling", "Mayling", "MaylingSSR01") });
        var m = merged.Single(c => c.Name == "Mayling");

        Assert.Equal(3, m.Outfits.Count);
        Assert.Equal("MaylingSSR01", m.Outfits[0].Stem);   // the DB's own model stays first, and stays first-class
        Assert.Null(m.Outfits[0].Route);
        Assert.Equal(new[] { "Mayling_dorm", "c_Mayling_dorm_nobag_skin_model" },
            m.Outfits.Skip(1).Select(o => o.Stem).ToArray());
    }

    [Fact]
    public void MergeInto_SkipsACuratedStemTheCharacterAlreadyLists()
    {
        // if the game ever ships a ModelConfig row for this stem, the DB's version wins and the curated
        // duplicate does NOT list beside it
        var merged = CuratedSkins.MergeInto(new[] { Db("Mayling", "Mayling", "Mayling_dorm") });
        var m = merged.Single(c => c.Name == "Mayling");

        Assert.Equal(OutfitKind.Base, m.Outfits[0].Kind);   // the DB row, not the curated one
        Assert.Null(m.Outfits[0].Route);
        // and only that stem is skipped: the character's OTHER curated build still lists
        Assert.Equal(new[] { "Mayling_dorm", "c_Mayling_dorm_nobag_skin_model" },
            m.Outfits.Select(o => o.Stem).ToArray());
    }

    [Fact]
    public void MergeInto_LabelsWithTheCuratedName_WhenTheDbLeftTheCharacterNameless()
    {
        var merged = CuratedSkins.MergeInto(new[] { Db("Mayling", display: null) });
        Assert.Equal("Mayling", merged.Single(c => c.Name == "Mayling").DisplayName);
    }

    [Fact]
    public void MergeInto_SortsTheWholeRosterByName_LikeTheDbReadDoes()
    {
        var merged = CuratedSkins.MergeInto(new[] { Db("Zed", "Zed"), Db("Aral", "Aral") });
        Assert.Equal(merged.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
            merged.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Enrich_LeavesACuratedLabelAlone_WhenNoLocalizedNameResolves()
    {
        // Enrich runs before the merge today, but it must not be a landmine either way: a lookup miss
        // filling null over a curated string would blank exactly the labels this feature exists to show.
        using var g = new TempGame();
        g.WriteTable("GunCharacterData", TempGame.TableBytes(new[]
        {
            TempGame.GunCharRowLoc(charId: 1, name: "Wren", family: "", gunId: 7, dormCfg: 0, nameTextId: null),
        }));
        var root = g.WriteTable("LangPackageTableEnusData", TempGame.TableBytes(new[] { TempGame.LangRow(1, "x") }));
        var db = GameDatabase.FromGameDir(root);
        var names = DisplayNames.Build(db, LocalizationDb.Load(db));

        var curated = CuratedSkins.MergeInto(Array.Empty<Character>());
        var enriched = names.Enrich(curated);

        var male = enriched.Single(c => c.Name == "CommanderMale");
        Assert.Equal("Commander (M)", male.DisplayName);
        Assert.Equal(new[] { "001", "002", "003", "Neutral" }, male.Outfits.Select(o => o.DisplayName).ToArray());
    }

    // ---- the roster provider: the three characters reach Pick --------------------------------------

    [Fact]
    public void Roster_SurfacesTheThreeCuratedCharacters_AlongsideTheDbOnes()
    {
        using var g = new TempGame();
        g.WriteTable("GunCharacterData", TempGame.TableBytes(new[]
        {
            TempGame.GunCharRow(charId: 42, name: "Wren", family: "WrenSSR", gunId: 1071, dormCfg: 0),
        }));
        g.WriteTable("BattleSummonedData", TempGame.TableBytes(Array.Empty<byte[]>()));
        var root = g.WriteTable("ModelConfigData", TempGame.TableBytes(new[]
        {
            TempGame.ModelConfigRow(1071, "WrenSSR01"),
        }));

        var roster = CuratedSkins.MergeInto(GameDatabase.FromGameDir(root).ReadRoster());

        Assert.Equal(new[] { "CommanderFemale", "CommanderMale", "Mayling", "Wren" },
            roster.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { "Commander (F)", "Commander (M)", "Mayling", "Wren" },
            roster.Select(FriendlyNames.Label).ToArray());
        Assert.Equal(9, roster.SelectMany(c => c.Outfits).Count(o => o.ModelConfigId < 0));
        Assert.Equal(8, roster.SelectMany(c => c.Outfits).Count(o => o.Route is not null));
    }

    // ---- the resolution routes, over a synthetic corpus (invented names throughout) ----------------

    private static string Corpus(TempGame g)
    {
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        return abw;
    }

    /// <summary>A one-root prefab whose slots carry serialized meshes — the direct-SMR shape the NPC and
    /// commander bodies ship in (no RoleMeshRes recipe).</summary>
    private static void SmrPrefab(string abw, char fill, string bundleName, string rootName, string meshPrefix)
    {
        WorkbenchPrefab.Build(Path.Combine(abw, new string(fill, 32) + ".bundle"),
            bundleName: bundleName, rootName: rootName,
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec($"{meshPrefix}face_lod0", new[] { (0, 0L) }, Mesh: (0, 901L)),
                new WorkbenchPrefab.SlotSpec($"{meshPrefix}body_lod0", new[] { (0, 0L) }, Mesh: (0, 902L)),
                new WorkbenchPrefab.SlotSpec($"{meshPrefix}body_lod1", new[] { (0, 0L) }, Mesh: (0, 903L)),
            },
            recipe: Array.Empty<(string, string)>(),
            externalCabs: Array.Empty<string>(),
            bones: new[] { ("Bip001", -1), ("Bip001 Spine", 0) });
    }

    /// <summary>A catalog with NO address rows that still NAMES the given bundles — the direct-bundle
    /// shape's own world: nothing the address formula could ever hit, and the route is the only way in.
    /// The naming matters: a bundle the catalog doesn't name is not in this install's corpus, and both the
    /// scope and the launch existence gate refuse it on that test alone.</summary>
    private static CatalogIndex BundlesOnly(params string[] logical) =>
        CatalogIndex.ForTest(Array.Empty<(string, string)>(), null,
            logical.Select((b, i) => (b, new string((char)('a' + i), 32) + ".bundle")));

    [Fact]
    public void DirectBundleRoute_ReadsThePrefabAtItsNamedRoot_WithNoCatalogAddress()
    {
        using var g = new TempGame();
        var abw = Corpus(g);
        SmrPrefab(abw, '1', "curated.bundle", "Wren02", "c_Wren02_dorm_");

        var catalog = BundlesOnly("curated.bundle");
        var outfit = new Outfit(-99, "Wren02", OutfitKind.Other)
        {
            MeshPrefixOverride = "c_Wren02_dorm_",
            Route = SubjectRoute.DirectBundle("curated.bundle", "Wren02"),
        };

        var model = SubjectModelBuilder.Build(catalog, FixtureCrawl.DeobfuscateOver(abw), outfit, "Wren");

        Assert.Empty(model.Problems);
        Assert.Equal("Wren02", model.PrimaryRoot);
        Assert.Equal(new[] { "body", "face" }, model.Parts.Select(p => p.Token).ToArray());
        // the smr-body identity resolves to the route's own bundle
        var body = model.Parts.Single(p => p.Token == "body");
        Assert.Equal("curated.bundle", body.MeshBundle);
        Assert.Equal(902, body.MeshPathId);
        Assert.Equal("c_Wren02_dorm_body_lod1", Assert.Single(body.SiblingTiers!).SlotName);
    }

    [Fact]
    public void DirectBundleRoute_WithARootTheBundleDoesNotCarry_ResolvesNothingLoudly()
    {
        // The root name is load-bearing: a direct bundle can hold sibling roots, and taking whichever
        // parsed first would quietly hand this subject another model. A miss must be loud instead.
        using var g = new TempGame();
        var abw = Corpus(g);
        SmrPrefab(abw, '1', "curated.bundle", "Wren02", "c_Wren02_dorm_");

        var outfit = new Outfit(-99, "Wren03", OutfitKind.Other)
        {
            MeshPrefixOverride = "c_Wren03_dorm_",
            Route = SubjectRoute.DirectBundle("curated.bundle", "Wren03"),
        };
        var model = SubjectModelBuilder.Build(BundlesOnly("curated.bundle"),
            FixtureCrawl.DeobfuscateOver(abw), outfit, "Wren");

        Assert.Empty(model.Parts);
        Assert.Contains(model.Problems, p => p.Contains("No assembly prefab found", StringComparison.Ordinal));
    }

    [Fact]
    public void DirectBundleRoute_AbsentFromTheInstall_ResolvesNothingLoudly()
    {
        using var g = new TempGame();
        var abw = Corpus(g);
        SmrPrefab(abw, '1', "other.bundle", "Wren02", "c_Wren02_dorm_");

        var outfit = new Outfit(-99, "Wren02", OutfitKind.Other)
        {
            Route = SubjectRoute.DirectBundle("missing.bundle", "Wren02"),
        };
        // named by the catalog, so the guard passes and the miss is the READ finding nothing
        var model = SubjectModelBuilder.Build(BundlesOnly("missing.bundle"),
            FixtureCrawl.DeobfuscateOver(abw), outfit, "Wren");

        Assert.Empty(model.Parts);
        Assert.Contains(model.Problems, p => p.Contains("No assembly prefab found", StringComparison.Ordinal));
    }

    [Fact]
    public void DirectBundleRoute_ACatalogThatNamesNoSuchBundle_BuildsAnEmptyScope()
    {
        // The guard the launch existence gate applies (GameVfs.PrefabsFor): a bundle the catalog does not
        // name is not this install's, and the two surfaces must agree about that or the roster offers a
        // subject the workbench then can't open.
        var outfit = new Outfit(-99, "Wren02", OutfitKind.Other)
        {
            Route = SubjectRoute.DirectBundle("curated.bundle", "Wren02", "mats.bundle"),
        };
        byte[]? Refusing(string logical) =>
            throw new InvalidOperationException($"an empty scope must never read a bundle (asked for '{logical}')");

        var scope = SubjectScope.Build(CatalogIndex.ForTest(Array.Empty<(string, string)>()), Refusing, outfit);

        Assert.Empty(scope.ScopeBundles);
        Assert.Empty(scope.Candidates);
    }

    [Fact]
    public void DirectBundleRoute_PutsItsExtraBundlesInScope_SoMaterialsAndMapsResolve()
    {
        // A bare bundle has no catalog dependency row, so without the route's own extra bundles the
        // material CAB resolves to nothing and every map is a loud per-part problem.
        using var g = new TempGame();
        var abw = Corpus(g);
        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            bundleName: "curated.bundle", rootName: "Wren02",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_Wren02_dorm_body_lod0", new[] { (1, 21L) }, Mesh: (0, 902L)) },
            recipe: Array.Empty<(string, string)>(),
            externalCabs: new[] { "CAB-matA" },
            bones: new[] { ("Bip001", -1) });
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('2', 32) + ".bundle"),
            "mats.bundle", materialName: "M_body", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_Wren02_dorm_body_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)), cabName: "CAB-matA");

        var catalog = BundlesOnly("curated.bundle", "mats.bundle");
        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        Outfit Routed(params string[] extras) => new(-99, "Wren02", OutfitKind.Other)
        {
            MeshPrefixOverride = "c_Wren02_dorm_",
            Route = SubjectRoute.DirectBundle("curated.bundle", "Wren02", extras),
        };

        // without the extra bundle: the part lists, its material does not resolve, and it says so
        var without = SubjectModelBuilder.Build(catalog, deobfuscate, Routed(), "Wren");
        Assert.Equal(new[] { "body" }, without.Parts.Select(p => p.Token).ToArray());
        Assert.Empty(Assert.Single(without.Parts).Materials.SelectMany(m => m.Maps));
        Assert.Contains(without.Problems, p => p.Contains("CAB-matA", StringComparison.Ordinal));

        // with it: the same CAB-exact resolution the address route gets from its closure
        var with = SubjectModelBuilder.Build(catalog, deobfuscate, Routed("mats.bundle"), "Wren");
        Assert.Empty(with.Problems);
        var material = Assert.Single(Assert.Single(with.Parts).Materials);
        Assert.Equal("M_body", material.Name);
        Assert.Equal("c_Wren02_dorm_body_d", Assert.Single(material.Maps).TextureName);
        // the prefab bundle stays the one candidate source; the extras are dependencies, not prefabs
        Assert.Equal(new[] { "curated.bundle", "mats.bundle" },
            SubjectScope.Build(catalog, deobfuscate, Routed("mats.bundle")).ScopeBundles.ToArray());
    }

    [Fact]
    public void DirectBundleRoute_SameNamedTexturesInTwoScopes_EachResolveToTheirOwn()
    {
        // The shipped trap: one texture NAME ships as two different assets in two bundles, one per
        // subject. Resolution is CAB-exact, so each subject's scope answers with its own — a name-keyed
        // lookup would hand one of them the other's map.
        using var g = new TempGame();
        var abw = Corpus(g);
        var built = new List<string>();
        void Skin(char fill, char matFill, string stem, string prefabBundle, string matBundle, string cab, byte red)
        {
            WorkbenchPrefab.Build(Path.Combine(abw, new string(fill, 32) + ".bundle"),
                bundleName: prefabBundle, rootName: stem,
                slots: new[] { new WorkbenchPrefab.SlotSpec($"c_{stem}_dorm_body_lod0", new[] { (1, 21L) }, Mesh: (0, 902L)) },
                recipe: Array.Empty<(string, string)>(), externalCabs: new[] { cab },
                bones: new[] { ("Bip001", -1) });
            SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string(matFill, 32) + ".bundle"),
                matBundle, materialName: "M_body", materialPathId: 21,
                texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
                // the SAME texture name in both bundles, with different pixels
                localTexture: new SyntheticBundle.TextureSpec("c_Shared_dorm_body_d", 4, 4,
                    SyntheticBundle.SolidRgba32(4, 4, red, 0x22, 0x22, 0xFF)), cabName: cab);
            built.Add(prefabBundle);
            built.Add(matBundle);
        }
        Skin('1', '2', "WrenA", "a.bundle", "amats.bundle", "CAB-a", 0x11);
        Skin('3', '4', "WrenB", "b.bundle", "bmats.bundle", "CAB-b", 0x99);

        var catalog = BundlesOnly(built.ToArray());
        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        string BundleOfBodyMap(string stem, string prefabBundle, string matBundle)
        {
            var outfit = new Outfit(-1, stem, OutfitKind.Other)
            {
                MeshPrefixOverride = $"c_{stem}_dorm_",
                Route = SubjectRoute.DirectBundle(prefabBundle, stem, matBundle),
            };
            var model = SubjectModelBuilder.Build(catalog, deobfuscate, outfit, "Wren");
            Assert.Empty(model.Problems);
            var map = Assert.Single(Assert.Single(Assert.Single(model.Parts).Materials).Maps);
            Assert.Equal("c_Shared_dorm_body_d", map.TextureName);
            return map.BundleId;
        }

        Assert.Equal("amats.bundle", BundleOfBodyMap("WrenA", "a.bundle", "amats.bundle"));
        Assert.Equal("bmats.bundle", BundleOfBodyMap("WrenB", "b.bundle", "bmats.bundle"));
    }

    [Fact]
    public void AddressRoute_ResolvesThroughTheCatalog_IncludingItsDependencyClosure()
    {
        using var g = new TempGame();
        var abw = Corpus(g);
        // a recipe-backed prefab whose material lives in a SEPARATE bundle: it can only resolve if the
        // address route carried the catalog row's dependency closure into the scope
        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            bundleName: "prefab.bundle", rootName: "Wren_dorm",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_Wren_dorm_body_lod0", new[] { (1, 21L) }) },
            recipe: new[] { ("c_Wren_dorm_body_lod0", "Assets/X/c_Wren_dorm_body_lod0.mesh") },
            externalCabs: new[] { "CAB-matA" },
            bones: new[] { ("Bip001", -1) });
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('2', 32) + ".bundle"),
            "matA.bundle", materialName: "M_body", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_Wren_dorm_body_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)), cabName: "CAB-matA");

        // the address is NOT one the stem formula would ever build (the leaf differs from the stem), which
        // is exactly why this entry needs an explicit address
        const string address = "Assets/ConfigPrefab/BarrackModel/Character/Wren/Wren_dorm.prefab";
        var catalog = CatalogIndex.ForTest(
            new[] { (address, "prefab.bundle") },
            new[] { (address, new[] { "prefab.bundle", "matA.bundle" }) });
        var outfit = new Outfit(-98, "Wren_dorm", OutfitKind.Other)
        {
            MeshPrefixOverride = "c_Wren_dorm_",
            Route = SubjectRoute.Addressable(address, "Wren_dorm"),
        };

        var model = SubjectModelBuilder.Build(catalog, FixtureCrawl.DeobfuscateOver(abw), outfit, "Wren");

        Assert.Empty(model.Problems);
        Assert.Equal("Wren_dorm", model.PrimaryRoot);
        var body = Assert.Single(model.Parts);
        Assert.Equal("body", body.Token);
        Assert.Equal("M_body", Assert.Single(body.Materials).Name);          // the dep bundle was in scope
        Assert.Equal("c_Wren_dorm_body_d", Assert.Single(Assert.Single(body.Materials).Maps).TextureName);
    }

    [Fact]
    public void AddressRoute_PinsItsRootToo_NotJustTheDirectShape()
    {
        // A curated address's bundle can hold sibling roots — a shipped one pairs a prefab with a
        // prop-less variant of itself, and the variant is what "first root that parses" lands on. If the
        // address branch ever stops pinning, this resolves the one root present and the test fails.
        using var g = new TempGame();
        var abw = Corpus(g);
        SmrPrefab(abw, '1', "prefab.bundle", "Wren_dorm_nobag", "c_Wren_dorm_");

        const string address = "Assets/ConfigPrefab/BarrackModel/Character/Wren/Wren_dorm.prefab";
        var catalog = CatalogIndex.ForTest(new[] { (address, "prefab.bundle") });
        var outfit = new Outfit(-98, "Wren_dorm", OutfitKind.Other)
        {
            MeshPrefixOverride = "c_Wren_dorm_",
            Route = SubjectRoute.Addressable(address, "Wren_dorm"),
        };

        var model = SubjectModelBuilder.Build(catalog, FixtureCrawl.DeobfuscateOver(abw), outfit, "Wren");

        Assert.Empty(model.Parts);
        Assert.Contains(model.Problems, p => p.Contains("No assembly prefab found", StringComparison.Ordinal));
    }

    [Fact]
    public void AddressRoute_PinsOnlyItsOwnBundle_ClosureConstituentsContributeTheirSlots()
    {
        // The pin is about the ADDRESSED bundle holding sibling roots; a dependency bundle parses
        // first-root like the formula path's dependencies. Weapon subjects ship real constituent roots
        // that way — the addressed prefab carries the body recipe, and the closure's model bundle holds
        // the attachment assemblies under the subject's own mesh prefix.
        using var g = new TempGame();
        var abw = Corpus(g);
        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            bundleName: "prefab.bundle", rootName: "WrenSSR01_WL",
            slots: new[] { new WorkbenchPrefab.SlotSpec("cw_WrenSSR01_WL_lod0",
                Array.Empty<(int, long)>(), Mesh: (0, 902L)) },
            recipe: Array.Empty<(string, string)>(),
            externalCabs: Array.Empty<string>(),
            bones: new[] { ("Weapon", -1) });
        WorkbenchPrefab.Build(Path.Combine(abw, new string('2', 32) + ".bundle"),
            bundleName: "model.bundle", rootName: "cw_WrenSSR01_Sight_WL",
            slots: new[] { new WorkbenchPrefab.SlotSpec("cw_WrenSSR01_Sight_WL_lod0",
                Array.Empty<(int, long)>(), Mesh: (0, 903L), Renderer: SlotRenderer.Static) },
            recipe: null,
            externalCabs: Array.Empty<string>());

        const string address = "Assets/ConfigPrefab/Weapon/Player/WrenSSR01/WrenSSR01_WL.prefab";
        var catalog = CatalogIndex.ForTest(
            new[] { (address, "prefab.bundle") },
            new[] { (address, new[] { "prefab.bundle", "model.bundle" }) });
        var outfit = new Outfit(10133, "WrenSSR01_WL", OutfitKind.Other)
        {
            MeshPrefixOverride = "cw_WrenSSR01_",
            Route = SubjectRoute.Addressable(address, "WrenSSR01_WL"),
        };

        var scope = SubjectScope.Build(catalog, FixtureCrawl.DeobfuscateOver(abw), outfit);

        Assert.Equal(new[] { "WrenSSR01_WL", "cw_WrenSSR01_Sight_WL" },
            scope.Candidates.Select(c => c.Root).ToArray());
        Assert.Equal(new[] { "Sight_WL", "WL" },
            SubjectModelBuilder.OwnedSlotTokens(scope.Candidates, outfit).ToArray());
    }

    [Fact]
    public void ACuratedRoute_IsNeverAWayPastTheBlacklist()
    {
        // The blacklist is checked on the STEM before any route is read. If that order ever inverts, a
        // curated entry becomes a bypass — which is the one thing this list must not allow.
        using var g = new TempGame();
        var abw = Corpus(g);
        SmrPrefab(abw, '1', "curated.bundle", "Helena", "c_Helena_dorm_");

        var outfit = new Outfit(-97, "Helena", OutfitKind.Other)
        {
            Route = SubjectRoute.DirectBundle("curated.bundle", "Helena"),
        };
        var scope = SubjectScope.Build(CatalogIndex.ForTest(Array.Empty<(string, string)>()),
            FixtureCrawl.DeobfuscateOver(abw), outfit);

        Assert.Empty(scope.ScopeBundles);
        Assert.Empty(scope.Candidates);
    }

    // ---- the launch existence gate, the scope's cheap twin -----------------------------------------

    [Fact]
    public void PrefabsFor_Outfit_HonoursBothRouteShapes_AndTheFormulaWithoutOne()
    {
        using var g = new TempGame();
        g.WriteGameDir();
        const string address = "Assets/ConfigPrefab/BarrackModel/Character/Wren/Wren_dorm.prefab";
        var formulaAddress = GameVfs.PrefabAddress("Character/Player", "WrenSSR01");
        var vfs = TestVfs.Create(g.At(""),
            new[] { (address, "routed.bundle"), (formulaAddress, "formula.bundle") },
            depRows: null,
            ("routed.bundle", new string('a', 32)),
            ("formula.bundle", new string('b', 32)),
            ("direct.bundle", new string('c', 32)));

        Outfit Routed(string stem, SubjectRoute? route) =>
            new(-1, stem, OutfitKind.Other) { Route = route };

        // address route → the catalog row's owner
        var hit = Assert.Single(vfs.PrefabsFor(Routed("Wren_dorm", SubjectRoute.Addressable(address, "Wren_dorm"))));
        Assert.Equal("routed.bundle", hit.Bundle);
        Assert.Equal(GameVfs.CuratedContextRoot, hit.ContextRoot);

        // direct route → the catalog naming the bundle, a dictionary hit like the formula path's
        Assert.Equal("direct.bundle",
            Assert.Single(vfs.PrefabsFor(Routed("W", SubjectRoute.DirectBundle("direct.bundle", "W")))).Bundle);
        // a bundle the catalog doesn't name is simply absent
        Assert.Empty(vfs.PrefabsFor(Routed("W", SubjectRoute.DirectBundle("nowhere.bundle", "W"))));
        // an address the catalog doesn't name is likewise absent
        Assert.Empty(vfs.PrefabsFor(Routed("W", SubjectRoute.Addressable("Assets/ConfigPrefab/Nope/N/N.prefab", "N"))));

        // no route → unchanged formula behaviour
        Assert.Equal("formula.bundle", Assert.Single(vfs.PrefabsFor(Routed("WrenSSR01", null))).Bundle);
        Assert.Empty(vfs.PrefabsFor(Routed("Unknown01", null)));
    }

    [Fact]
    public void PrefabsFor_Outfit_StillRefusesABlacklistedStem_WhateverItsRoute()
    {
        using var g = new TempGame();
        g.WriteGameDir();
        var vfs = TestVfs.Create(g.At(""), Array.Empty<(string, string)>(), null,
            ("direct.bundle", new string('c', 32)));

        var outfit = new Outfit(-1, "Helena", OutfitKind.Other)
        {
            Route = SubjectRoute.DirectBundle("direct.bundle", "Helena"),
        };
        Assert.Empty(vfs.PrefabsFor(outfit));
    }

    // ---- engine-shared textures stay on the editable map surface -----------------------------------
    // A retexture of one is scoped at build time to the subject's own mesh draws (SharingScopeTests),
    // so the surface can show it without the edit reaching anything else.

    [Fact]
    public void EngineSharedTexture_StaysOnACuratedSubjectsMapList()
    {
        using var g = new TempGame();
        var abw = Corpus(g);
        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            bundleName: "curated.bundle", rootName: "CommanderMale",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_CommanderMale_dorm_hair_lod0", new[] { (1, 21L) }, Mesh: (0, 902L)) },
            recipe: Array.Empty<(string, string)>(),
            externalCabs: new[] { "CAB-matA" },
            bones: new[] { ("Bip001", -1) });
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('2', 32) + ".bundle"),
            "mats.bundle", materialName: "M_hair", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("skinblend", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0x10, 0x20, 0x30, 0xFF)), cabName: "CAB-matA");

        var catalog = BundlesOnly("curated.bundle", "mats.bundle");
        var outfit = new Outfit(-2, "CommanderMale", OutfitKind.Other)
        {
            MeshPrefixOverride = "c_CommanderMale_dorm_",
            Route = SubjectRoute.DirectBundle("curated.bundle", "CommanderMale", "mats.bundle"),
        };

        var model = SubjectModelBuilder.Build(catalog, FixtureCrawl.DeobfuscateOver(abw), outfit, "CommanderMale");

        var material = Assert.Single(Assert.Single(model.Parts).Materials);
        Assert.Equal("M_hair", material.Name);
        Assert.Null(material.Problem);
        Assert.Equal("skinblend", Assert.Single(material.Maps).TextureName);
    }
}
