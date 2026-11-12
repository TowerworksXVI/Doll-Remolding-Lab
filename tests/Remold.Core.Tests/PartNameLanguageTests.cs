using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.App.ViewModels.EditPage;
using Remold.Core.Project;
using Remold.Core.Tables;
using Remold.Core.Tests.Migoto;
using Remold.Core.Tests.Support;
using Remold.Core.Text;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>A part's name and the default names a new edit is given read in the UI language, while the
/// part's token stays the game's own spelling and a name already given stays as it was given.</summary>
[Collection("UiCulture")]
public class PartNameLanguageTests
{
    /// <summary>The shapes the game's part tokens take: bare and numbered words, numbered-piece prefixes,
    /// battle and dorm versions, weapon parts, the slg marker, words the catalog does not know, and whole
    /// mesh names a token can fall back to.</summary>
    public static readonly string[] Tokens =
    {
        "body", "cloth", "cloth1", "cloth10", "face", "hair", "weapon", "Weapon", "WL", "WL01", "WL_02", "boby",
        "addon", "Fight", "Dorm", "P1_body", "P2_cloth1_Fight", "P3_body1_Dorm", "cloth2_trans_Fight",
        "Silencer_WL", "Sight_WL", "slg_face", "Knife_weapon_slg", "slg", "slg_02", "trans", "trans1", "3_WL",
        "Stool_WL", "lod0", "01", "WL.001", "c_test_slg_body_lod0", "cw_test_WL_lod0", "",
    };

    private static void In(string language, Action check)
    {
        try
        {
            Catalog.Apply(language);
            check();
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    [Fact]
    public void In_english_every_part_name_is_the_token_itself()
    {
        foreach (string token in Tokens) Assert.Equal(token, PartNames.Display(token));
        foreach (string word in PartNames.KnownWords)
        {
            Assert.Equal(word, PartNames.Display(word));
            Assert.Equal(word.ToUpperInvariant(), PartNames.Display(word.ToUpperInvariant()));
            Assert.Equal($"P1_{word}2_Fight", PartNames.Display($"P1_{word}2_Fight"));
        }
    }

    [Fact]
    public void Under_the_pseudolocale_a_part_name_is_read_from_the_catalog_or_is_the_token()
    {
        In(Catalog.Pseudo, () =>
        {
            foreach (string token in Tokens)
            {
                string shown = PartNames.Display(token);
                Assert.True(shown == token || shown.Contains('['), $"'{token}' shows as '{shown}'");
            }
            foreach (string token in new[] { "body", "cloth1", "WL", "P1_body", "Silencer_WL", "slg_face", "addon" })
                Assert.Contains('[', PartNames.Display(token));
            // every word is its own entry, whose English is the word as the game spells it
            foreach (string word in PartNames.KnownWords)
                Assert.Equal($"[{word}]", PartNames.Display(word), ignoreCase: true);
        });
    }

    [Theory]
    [InlineData("body", "身体")]
    [InlineData("cloth2", "衣物2")]
    [InlineData("cloth10", "衣物10")]
    [InlineData("WL", "武器模型")]
    [InlineData("WL01", "武器模型01")]
    [InlineData("weapon", "武器")]
    [InlineData("P1_body_Fight", "P1_身体_战斗")]
    [InlineData("cloth2_trans_Fight", "衣物2_trans_战斗")]
    [InlineData("Silencer_WL", "消音器_武器模型")]
    [InlineData("slg_face", "slg_脸")]
    [InlineData("addon", "附加部件")]
    [InlineData("trans", "trans")]
    [InlineData("3_WL", "3_WL")]
    [InlineData("slg", "slg")]
    [InlineData("c_test_slg_body_lod0", "c_test_slg_body_lod0")]
    [InlineData("", "")]
    public void In_chinese_a_part_name_reads_word_by_word_and_keeps_numbers_prefixes_and_unknown_words(
        string token, string shown) =>
        In(Catalog.SimplifiedChinese, () => Assert.Equal(shown, PartNames.Display(token)));

    [Fact]
    public void In_chinese_new_edits_and_a_hide_are_named_in_chinese_and_numbered_as_english_ones_are()
    {
        In(Catalog.SimplifiedChinese, () =>
        {
            var session = new AuthoredEditSession(AuthoredEditFixtures.SlotsOnly());
            var body = AuthoredEditFixtures.Body;

            string first = session.CreateEdit(body);
            string second = session.CreateEdit(body);
            string hide = session.AddHideEdit(body);

            Assert.Equal("修改1", Label(session, first));
            Assert.Equal("修改2", Label(session, second));
            Assert.Equal("隐藏", Label(session, hide));

            // a cleared name puts back the default, minted in the language in force now
            session.RenameEdit(first, "Long coat");
            session.RenameEdit(first, " ");
            session.RenameEdit(hide, "Off");
            session.RenameEdit(hide, "");
            Assert.Equal("修改1", Label(session, first));
            Assert.Equal("隐藏", Label(session, hide));
        });
    }

    [Fact]
    public void A_project_named_in_english_keeps_its_names_in_chinese_and_the_next_edit_numbers_after_them()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.SlotsOnly());
        var body = AuthoredEditFixtures.Body;
        string english = session.CreateEdit(body);
        string englishHide = session.AddHideEdit(body);

        In(Catalog.SimplifiedChinese, () =>
        {
            string chinese = session.CreateEdit(body);

            Assert.Equal("Edit 1", Label(session, english));
            Assert.Equal("Hidden", Label(session, englishHide));
            Assert.Equal("修改2", Label(session, chinese));
        });
    }

    [Fact]
    public void Under_the_pseudolocale_the_default_names_come_from_the_catalog()
    {
        In(Catalog.Pseudo, () =>
        {
            var session = new AuthoredEditSession(AuthoredEditFixtures.SlotsOnly());
            var body = AuthoredEditFixtures.Body;

            Assert.Equal("[Edit 1]", Label(session, session.CreateEdit(body)));
            Assert.Equal("[Edit 2]", Label(session, session.CreateEdit(body)));
            Assert.Equal("[Hidden]", Label(session, session.AddHideEdit(body)));
        });
    }

    [Fact]
    public void In_chinese_a_picture_back_from_blender_without_a_file_name_is_named_in_chinese()
    {
        In(Catalog.SimplifiedChinese, () =>
        {
            Assert.Equal("材质基础色", MainWindowViewModel.BlenderMapLabel(
                null, null, TargetInputKind.BaseColor, "_BaseMap"));
            Assert.Equal("材质法线贴图", MainWindowViewModel.BlenderMapLabel(
                "", " ", TargetInputKind.Normal, null));
            Assert.Equal("M_body基础色", MainWindowViewModel.BlenderMapLabel(
                null, "M_body", TargetInputKind.BaseColor, "_BaseMap"));
            // a picture that came back with its own file name keeps it in every language
            Assert.Equal("painted_body", MainWindowViewModel.BlenderMapLabel(
                "painted_body.png", "M_body", TargetInputKind.BaseColor, "_BaseMap"));
        });
    }

    [Fact]
    public void An_older_project_converted_in_chinese_names_its_edits_in_chinese()
    {
        using var fixtures = new LegacyProjectAdapterTests();
        var legacy = fixtures.KeyedFixture(coatKey: "F7", coatStartsOff: false);

        In(Catalog.SimplifiedChinese, () =>
        {
            var adapted = LegacyProjectAdapter.Adapt(legacy, LegacyProjectAdapterTests.ResolveFixturePart);

            var hides = adapted.Project.EditDefinitions.Where(edit => edit.Kind == EditDefinitionKind.Hide).ToList();
            Assert.NotEmpty(hides);
            Assert.All(hides, edit => Assert.Equal("隐藏", edit.Label));
            Assert.Contains(adapted.Project.EditDefinitions, edit =>
                edit.Kind == EditDefinitionKind.Content && edit.Label == "修改1");
        });
    }

    [Fact]
    public void In_chinese_the_edit_tree_names_a_part_in_chinese_and_still_finds_it_by_its_token()
    {
        In(Catalog.SimplifiedChinese, () =>
        {
            var row = EditPageVmTests.BodyRowNamed(new AuthoredEditSession(AuthoredEditFixtures.Golden()), "cloth2");

            Assert.Equal("衣物2", row.Title);
            Assert.Equal("衣物2", row.InspectorHeader);
            Assert.Equal(AuthoredEditFixtures.Body.RendererSlot, row.Detail);
            Assert.All(row.Children, edit => Assert.StartsWith("衣物2 · ", edit.InspectorHeader));
            Assert.True(row.SelfMatches(new[] { "cloth2" }));
        });
    }

    [Theory]
    [InlineData("DollA01", "c_DollA01_slg_body_lod0", "body", "身体")]
    [InlineData("DollA01", "c_DollA01_slg_cloth1_lod0_Fight", "cloth1_Fight", "衣物1_战斗")]
    [InlineData("DollA01", "c_DollA01_slg_P1_body1_lod0", "P1_body1", "P1_身体1")]
    [InlineData("SummonA01", "c_FamilyB_slg_body_lod0", "slg_body", "slg_身体")]
    [InlineData("DollA01", "c_doll_body_lod0", "body", "身体")]
    public void A_plan_line_names_a_part_as_the_edit_tree_does(string outfit, string rendererSlot, string english,
        string chinese)
    {
        var part = new TargetPart { Subject = "Doll", Outfit = outfit, RendererSlot = rendererSlot };
        Assert.Equal(english, AuthoredBuildPlanner.ShownPartName(part));
        In(Catalog.SimplifiedChinese, () => Assert.Equal(chinese, AuthoredBuildPlanner.ShownPartName(part)));
    }

    [Fact]
    public void In_chinese_a_plan_line_reads_a_numbered_piece_of_the_body_in_chinese()
    {
        using var plans = new KeyGroupPlanTests();
        In(Catalog.SimplifiedChinese, () =>
        {
            var plan = plans.PlanHidingTheBodyUnder($"c_{AuthoredEditFixtures.Body.Outfit}_slg_P1_body1_lod0");

            Assert.Contains(CoreStrings.PlanNeverAppears("Long body", "P1_身体1", PlacementNames.Always),
                plan.Warnings);
        });
    }

    [Fact]
    public void A_build_warning_names_a_numbered_piece_of_the_body_in_its_own_case_and_in_chinese()
    {
        using (var english = new RigidReplaceTests())
            Assert.Contains(CoreStrings.BuildAmbiguousMaterials("P1_body1"),
                english.AmbiguousPartWarnings("P1_body1", "p_CrateMk2_P1_body1_lod0", "p_CrateMk2_P1_body1_lod1"));

        using var chinese = new RigidReplaceTests();
        In(Catalog.SimplifiedChinese, () => Assert.Contains(CoreStrings.BuildAmbiguousMaterials("P1_身体1"),
            chinese.AmbiguousPartWarnings("P1_body1", "p_CrateMk2_P1_body1_lod0", "p_CrateMk2_P1_body1_lod1")));
    }

    [Fact]
    public void In_chinese_the_shading_source_list_names_a_part_in_chinese_and_still_finds_it_by_its_token()
    {
        In(Catalog.SimplifiedChinese, () =>
        {
            var shell = new MainWindowViewModel(startLoad: false);
            var subjects = new[] { (Subject: "Doll", Outfit: "DollA01") };
            shell.SubjectModels.GetOrBuild("Doll", "DollA01",
                () => new SubjectModel("Doll", "DollA01", SubjectSource.Prefab, new[]
                {
                    new SubjectPart("cloth2", "cloth2", "cloth2-address", new[]
                    {
                        new SubjectMaterial("Skin", 74001, "cab", Array.Empty<SubjectMap>(), Bundle: "cloth.bundle"),
                    }),
                }, Skeleton: null, Problems: Array.Empty<string>()));
            var target = new TargetPart { Subject = "Target", Outfit = "Target01", RendererSlot = "target" };
            var picker = new ShadingSourcePickerVm();
            picker.SetRows(shell.ShadingSourceRows(target, 0, subjects, "fixture", CancellationToken.None).Rows);
            picker.Read = EditSubjectRead.Answered;

            var row = Assert.Single(picker.Rows);
            Assert.Equal("衣物2", row.PartLabel);
            picker.Filter = "cloth2";
            Assert.Equal(new[] { row }, picker.Visible.ToArray());
            picker.Filter = "衣物2";
            Assert.Equal(new[] { row }, picker.Visible.ToArray());
        });
    }

    [UiFact]
    public async Task In_chinese_the_build_board_names_a_part_in_chinese()
    {
        try
        {
            Catalog.Apply(Catalog.SimplifiedChinese);
            var vm = await BuildPageVmTests.PageOver(AuthoredEditFixtures.Golden());

            Assert.Equal("身体", Assert.Single(Assert.Single(vm.Subjects).Parts).Label);
            Assert.NotEmpty(vm.Always.Tokens);
            Assert.All(vm.Always.Tokens, token => Assert.StartsWith("身体 · ", token.Display));
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    private static string Label(AuthoredEditSession session, string editId) =>
        session.Snapshot().EditDefinitions.Single(edit => edit.Id == editId).Label;
}
