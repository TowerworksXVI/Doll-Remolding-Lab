using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Remold.App.Textures;
using Remold.App.ViewModels;
using Remold.App.ViewModels.EditPage;
using Remold.Core;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

[Collection("Dispatcher")]
public sealed class SessionRampRouteTests
{
    [Fact]
    public void Ramp_image_refuses_the_wrong_extent()
    {
        using var game = new TempGame();
        string path = WriteDds(game.At("wide.dds"), DdsWriter.R16G16B16A16_FLOAT, 128, 16);

        string refusal = Assert.IsType<string>(RampImage.RefuseAsRamp(path));

        Assert.Contains("128\u00d716", refusal);
        Assert.Contains("256\u00d716", refusal);
    }

    [Fact]
    public void Ramp_image_refuses_a_non_float_ramp()
    {
        using var game = new TempGame();
        string path = WriteDds(game.At("rgba8.dds"), DdsWriter.R8G8B8A8_UNORM, 256, 16);

        string refusal = Assert.IsType<string>(RampImage.RefuseAsRamp(path));

        Assert.Contains(RampImage.Requirement, refusal);
    }

    [Fact]
    public void Ramp_image_refuses_a_non_dds_file()
    {
        using var game = new TempGame();
        string path = game.At("picture.dds");
        File.WriteAllText(path, "not a DDS");

        string refusal = Assert.IsType<string>(RampImage.RefuseAsRamp(path));

        Assert.Contains(RampImage.Requirement, refusal);
    }

    [Fact]
    public void Ramp_image_refuses_short_bytes()
    {
        using var game = new TempGame();
        string path = WriteDds(game.At("short.dds"), DdsWriter.R16G16B16A16_FLOAT, 256, 16);
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^1]);

        string refusal = Assert.IsType<string>(RampImage.RefuseAsRamp(path));

        Assert.Contains(RampImage.Requirement, refusal);
    }

    [Fact]
    public void Ramp_image_accepts_a_valid_preview()
    {
        EnsureAvalonia();
        byte[] fp16 = FloatRamp(256, 16, 0.5f);

        using var preview = RampImage.TryPreview(256, 16, fp16);

        Assert.NotNull(preview);
        Assert.Equal(new PixelSize(256, 64), preview.PixelSize);
    }

    [Fact]
    public void Project_dds_ramp_uses_the_ramp_preview_decoder()
    {
        EnsureAvalonia();
        using var game = new TempGame();
        string path = WriteDds(game.At("project-ramp.dds"),
            DdsWriter.R16G16B16A16_FLOAT, 256, 16);

        var preview = EditPreviewService.DecodeProjectMap(path, rmo: false);

        using var image = preview.Image;
        Assert.NotNull(image);
        Assert.Equal(new PixelSize(256, 64), image.PixelSize);
        Assert.Equal("256\u00d716", preview.Dimensions);
    }

    [Fact]
    public async Task Picker_filter_never_hides_the_materials_own_row()
    {
        var own = Row("Body · Skin", RampChoice.KeepOwn, own: true);
        var other = Row("Hair · Bangs", new RampChoice("hair.bundle", "ramp", null));
        var picker = Picker(new[] { own, other });
        await picker.LoadAsync();

        picker.Filter = "does-not-match-any-row";

        Assert.Equal(new[] { own }, picker.Visible.ToArray());
        picker.Dispose();
    }

    [Fact]
    public void Shading_source_filter_matches_part_and_material_words()
    {
        var body = new ShadingSourceRow("Body", "Skin", new object());
        var hair = new ShadingSourceRow("Hair", "Bangs", new object());
        var picker = new ShadingSourcePickerVm();
        picker.SetRows(new[] { body, hair });
        picker.Read = EditSubjectRead.Answered;

        picker.Filter = "body skin";
        Assert.Equal(new[] { body }, picker.Visible.ToArray());

        picker.Filter = "BANGS";
        Assert.Equal(new[] { hair }, picker.Visible.ToArray());

        picker.Filter = "missing";
        Assert.Empty(picker.Visible);
        Assert.Equal("No match for 'missing'.", picker.StateLine);
    }

    [Fact]
    public async Task Shading_sources_stay_truthfully_reading_and_fold_in_each_warm_subject()
    {
        var shell = new MainWindowViewModel(startLoad: false);
        var subjects = new[] { (Subject: "Vesna", Outfit: "VesnaSSR01"),
            (Subject: "Karst", Outfit: "KarstDorm") };
        shell.SubjectModels.GetOrBuild(subjects[0].Subject, subjects[0].Outfit,
            () => ShadingModel(subjects[0].Subject, subjects[0].Outfit, "body", 74001));
        var target = new TargetPart
        {
            Subject = "Target", Outfit = "Target01", RendererSlot = "target",
        };
        var picker = new ShadingSourcePickerVm();
        int waits = 0;

        await picker.LoadAsync(gone => Task.FromResult(shell.ShadingSourceRows(target, 0, subjects,
                "fixture", gone)), CancellationToken.None, _ =>
        {
            waits++;
            Assert.Equal(GameFilesGate.SubjectReading, picker.StateLine);
            Assert.NotEqual(ShadingSourcePickerVm.NoRowsLine, picker.StateLine);
            Assert.Single(picker.Rows);
            shell.SubjectModels.GetOrBuild(subjects[1].Subject, subjects[1].Outfit,
                () => ShadingModel(subjects[1].Subject, subjects[1].Outfit, "coat", 74002));
            return Task.CompletedTask;
        });

        Assert.Equal(1, waits);
        Assert.Equal(EditSubjectRead.Answered, picker.Read);
        Assert.Equal(new[] { "body · Vesna", "coat · Karst" },
            picker.Rows.Select(row => row.PartLabel));
        Assert.Equal("", picker.StateLine);

        picker.SetRows(Array.Empty<ShadingSourceRow>());
        picker.Read = EditSubjectRead.Reading;
        Assert.Equal(GameFilesGate.SubjectReading, picker.StateLine);
        picker.Read = EditSubjectRead.Answered;
        Assert.Equal(ShadingSourcePickerVm.NoRowsLine, picker.StateLine);
    }

    [Fact]
    public async Task Chooser_settles_from_warm_notifications_without_poll_or_duplicate_rows()
    {
        var shell = new MainWindowViewModel(startLoad: false);
        var subjects = new[] { (Subject: "Vesna", Outfit: "VesnaSSR01"),
            (Subject: "Karst", Outfit: "KarstDorm") };
        shell.SubjectModels.GetOrBuild(subjects[0].Subject, subjects[0].Outfit,
            () => ShadingModel(subjects[0].Subject, subjects[0].Outfit, "body", 74001));
        var target = new TargetPart { Subject = "Target", Outfit = "Target01", RendererSlot = "target" };
        var picker = new ShadingSourcePickerVm();
        int loads = 0;
        int notifications = 0;

        var settled = picker.LoadAsync(gone =>
        {
            loads++;
            return Task.FromResult(shell.ShadingSourceRows(target, 0, subjects, "fixture", gone));
        }, CancellationToken.None, (version, gone) =>
        {
            notifications++;
            return shell.SubjectModels.WaitForChangeAsync(version, gone);
        });
        for (int i = 0; i < 200 && picker.Rows.Count != 1; i++) await Task.Delay(5);
        Assert.Single(picker.Rows);

        shell.SubjectModels.GetOrBuild(subjects[1].Subject, subjects[1].Outfit,
            () => ShadingModel(subjects[1].Subject, subjects[1].Outfit, "coat", 74002));
        shell.SubjectModels.GetOrBuild(subjects[1].Subject, subjects[1].Outfit,
            () => throw new InvalidOperationException("a warm hit must not rebuild"));
        await settled;

        Assert.Equal(2, loads);
        Assert.Equal(1, notifications);
        Assert.Equal(2, picker.Rows.Count);
        Assert.Equal(2, picker.Rows.Select(row => row.Tag).Distinct().Count());
    }

    [Fact]
    public void Ramp_cache_serves_a_second_open_and_misses_after_rescan_or_install_replacement()
    {
        EnsureAvalonia();
        var cache = new InstallRampCache();
        var install = new object();
        var replacement = new object();
        var choice = new RampChoice("body.bundle", "ramp", null, 74001);
        var read = new RampImage.Read(256, 16, FloatRamp(256, 16, 0.5f));
        var entry = new InstallRampCache.Entry(read, SessionRampRows.RenderPreview(read));
        cache.Store(install, "catalog-a", choice, entry);

        Assert.True(cache.TryGet(install, "catalog-a", choice, out var secondOpen));
        Assert.Same(entry, secondOpen);
        var rows = SessionRampRows.Fold(new[]
        {
            new SessionRampReadCandidate(choice, new[] { "Body · Skin" }, false, true,
                secondOpen!.Read, secondOpen.PreviewPng),
        });
        var row = Assert.Single(rows.Rows);
        Assert.Equal(choice, row.Choice);
        Assert.Equal("256×16", row.Dimensions);
        Assert.True(row.HasThumb);
        row.Thumbnail?.Dispose();
        Assert.False(cache.TryGet(replacement, "catalog-a", choice, out _));

        cache.Store(install, "catalog-a", choice, entry);
        cache.Clear();
        Assert.False(cache.TryGet(install, "catalog-a", choice, out _));
    }

    [Fact]
    public async Task Picker_refused_import_adds_no_row()
    {
        var own = Row("Body · Skin", RampChoice.KeepOwn, own: true);
        var picker = Picker(new[] { own }, _ => "wrong ramp");
        picker.ChooseImportFile = () => Task.FromResult<string?>("wrong.dds");
        await picker.LoadAsync();

        await picker.ImportCommand.ExecuteAsync(null);

        Assert.Equal(new[] { own }, picker.Rows.ToArray());
        Assert.Equal("wrong ramp", picker.Refusal);
        picker.Dispose();
    }

    [Fact]
    public async Task Picker_clean_empty_list_uses_the_empty_state_wording()
    {
        var picker = Picker(Array.Empty<RampPickRowVm>());

        await picker.LoadAsync();

        Assert.True(picker.HasNoRows);
        Assert.Equal("No toon ramps were found.", RampPickerVm.NoRowsLine);
        picker.Dispose();
    }

    [Fact]
    public void Shared_session_ramp_content_folds_to_one_named_row()
    {
        byte[] fp16 = FloatRamp(256, 16, 0.25f);
        var read1 = new RampImage.Read(256, 16, (byte[])fp16.Clone());
        var read2 = new RampImage.Read(256, 16, (byte[])fp16.Clone());
        var choice = new RampChoice("body.bundle", "ramp", null, 101);

        RampPickLoad load = SessionRampRows.Fold(new[]
        {
            new SessionRampReadCandidate(choice, new[] { "Vesna · Default · Body" }, false, false, read1),
            new SessionRampReadCandidate(new RampChoice("coat.bundle", "ramp", null, 202),
                new[] { "Vesna · Default · Coat" }, false, true, read2),
        });

        var row = Assert.Single(load.Rows);
        Assert.Equal(choice, row.Choice);
        Assert.Equal("Vesna · Default · Body", row.Title);
        Assert.Equal("and 1 more", row.Source);
        Assert.Contains("Vesna · Default · Body", row.SourcesTip);
        Assert.Contains("Vesna · Default · Coat", row.SourcesTip);
        Assert.True(row.IsBound);
        row.Thumbnail?.Dispose();
    }

    [Fact]
    public async Task Picker_pins_and_selects_the_own_row_for_an_unpicked_slot()
    {
        byte[] fp16 = FloatRamp(256, 16, 0.75f);
        RampPickLoad load = SessionRampRows.Fold(new[]
        {
            new SessionRampReadCandidate(RampChoice.KeepOwn, new[] { "Body · Skin · its ramp" },
                true, true, new RampImage.Read(256, 16, fp16)),
            new SessionRampReadCandidate(new RampChoice("hair.bundle", "ramp", null, 42),
                new[] { "Hair · Bangs" }, false, false, new RampImage.Read(256, 16, (byte[])fp16.Clone())),
        });
        var picker = new RampPickerVm(_ => Task.FromResult(load), _ => null, "Skin");

        await picker.LoadAsync();

        Assert.True(picker.Rows[0].IsOwn);
        Assert.Same(picker.Rows[0], picker.Selected);
        Assert.True(picker.Selected!.IsBound);
        Assert.Equal(RampChoice.KeepOwn, picker.Selected.Choice);
        picker.Dispose();
    }

    [Fact]
    public async Task Picker_export_cancel_writes_and_reports_nothing()
    {
        var picker = Picker(new[] { Row("Body ramp", new RampChoice("body.bundle", "ramp", null)) });
        await picker.LoadAsync();
        bool wrote = false;
        picker.ChooseExportPath = _ => Task.FromResult<string?>(null);
        picker.ExportTo = (_, _) => { wrote = true; return Task.FromResult<string?>(null); };

        await picker.ExportCommand.ExecuteAsync(null);

        Assert.False(wrote);
        Assert.Null(picker.Note);
        Assert.Null(picker.Refusal);
        picker.Dispose();
    }

    [Fact]
    public async Task Picker_successful_export_reports_the_destination_file()
    {
        var picker = Picker(new[] { Row("Body ramp", new RampChoice("body.bundle", "ramp", null)) });
        await picker.LoadAsync();
        string? suggestion = null;
        RampChoice? written = null;
        picker.ChooseExportPath = name =>
        {
            suggestion = name;
            return Task.FromResult<string?>(Path.Combine("exports", "chosen.dds"));
        };
        picker.ExportTo = (choice, _) =>
        {
            written = choice;
            return Task.FromResult<string?>(null);
        };

        await picker.ExportCommand.ExecuteAsync(null);

        Assert.Equal("Body ramp.dds", suggestion);
        Assert.Equal(picker.Selected!.Choice, written);
        Assert.Equal("\u2713 Exported chosen.dds", picker.Note);
        picker.Dispose();
    }

    [Fact]
    public async Task Picker_apply_resolves_the_choice_and_closes_as_applied()
    {
        var choice = new RampChoice("body.bundle", "ramp", null, 88);
        var picker = Picker(new[] { Row("Body ramp", choice) });
        await picker.LoadAsync();
        bool? closed = null;
        picker.Close = applied => closed = applied;

        picker.ApplyCommand.Execute(null);

        Assert.Equal(choice, picker.Result);
        Assert.True(closed);
        picker.Dispose();
    }

    private static RampPickerVm Picker(IReadOnlyList<RampPickRowVm> rows,
        Func<string, string?>? refuse = null) => new(
        _ => Task.FromResult(new RampPickLoad(rows)), refuse ?? (_ => null), "Skin");

    private static RampPickRowVm Row(string title, RampChoice choice, bool own = false,
        bool bound = false) => new RampPickRowVm
        {
            Choice = choice,
            Title = title,
            IsOwn = own,
            IsBound = bound,
        }.Settled(null);

    private static SubjectModel ShadingModel(string subject, string outfit, string part, long pathId) =>
        new(subject, outfit, SubjectSource.Prefab, new[]
        {
            new SubjectPart(part, part, part + "-address", new[]
            {
                new SubjectMaterial(part + " material", pathId, "cab",
                    Array.Empty<SubjectMap>(), Bundle: part + ".bundle"),
            }),
        }, Skeleton: null, Problems: Array.Empty<string>());

    private static string WriteDds(string path, uint format, int width, int height)
    {
        int bytesPerPixel = DdsWriter.BytesPerPixel(format);
        using var stream = File.Create(path);
        DdsWriter.Write(stream, format, width, height,
            new[] { new byte[checked(width * height * bytesPerPixel)] });
        return path;
    }

    private static byte[] FloatRamp(int width, int height, float value)
    {
        byte[] half = BitConverter.GetBytes((Half)value);
        var bytes = new byte[checked(width * height * 8)];
        for (int at = 0; at < bytes.Length; at += 2)
        {
            bytes[at] = half[0];
            bytes[at + 1] = half[1];
        }
        return bytes;
    }

    private static bool _avaloniaReady;

    private static void EnsureAvalonia()
    {
        if (_avaloniaReady) return;
        AppBuilder.Configure<Remold.App.App>().UsePlatformDetect().SetupWithoutStarting();
        _avaloniaReady = true;
    }
}
