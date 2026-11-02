using System.IO.Compression;
using System.Numerics;
using UskEffect.Core.Export;
using UskEffect.Core.Project;
using UskEffect.Graphics;
using UskEffect.Graphics.Effects;

namespace UskEffect.Tests;

public sealed class ProjectFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uskfx-test").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathFor(string name) => Path.Combine(_dir, name);

    // すべての項目を既定値以外にして、保存漏れ・読込漏れを検出する
    private static ProjectDocument NonDefault() => new()
    {
        Noise = new NoiseSettings
        {
            Resolution = 1024, Type = NoiseType.Simplex, Seed = 42, Frequency = 7, Octaves = 3, Persistence = 0.35f,
            Seam = SeamMode.X, Lacunarity = 3, Fractal = FractalMode.Ridged, Worley = WorleyMode.F2MinusF1,
            WarpStrength = 0.2f, Contrast = 1.7f, Brightness = -0.1f, Invert = true,
        },
        Preview = new PreviewSettings
        {
            Mode = PreviewMode.Effect, ScrollSpeedU = 0.3f, ScrollSpeedV = -0.2f,
            Polar = new PolarSettings(0.1f, 0.9f, 45f, FlipAngular: true, FlipRadial: true),
            Heatmap = HeatmapMode.Gradient, HeatThreshold = 7f,
        },
        Effect = new EffectSettings
        {
            Mapping = EffectMapping.Polar, Blend = EffectBlend.Alpha, Background = EffectBackground.Gray,
            Mask = EffectMask.None, Rotation = new(10f, 200f), Scale = new(0.5f, 1.5f), Dissolve = new(0.1f, 0.9f),
            DissolveEdgeWidth = 0.2f, DissolveEdgeColor = new Vector3(0.1f, 0.2f, 0.3f), Alpha = new(0.8f, 0.2f),
            Color = new(new Vector3(1f, 0.5f, 0f), new Vector3(0f, 0.5f, 1f)), TextureAsAlpha = false,
        },
        Timeline = new TimelineSettings { Duration = 3.5, Loop = false },
        Export = new ExportSettings { Channels = ExportChannels.RgbaWhiteAlpha },
    };

    [Fact]
    public void SaveThenLoad_RestoresEverySetting()
    {
        var original = NonDefault();
        string path = PathFor("a.uskfx");

        ProjectFile.Save(path, original);
        var loaded = ProjectFile.Load(path);

        Assert.Equal(original.Noise, loaded.Noise);
        Assert.Equal(original.Preview, loaded.Preview);
        Assert.Equal(original.Effect, loaded.Effect);
        Assert.Equal(original.Timeline, loaded.Timeline);
        Assert.Equal(original.Export, loaded.Export);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void File_IsZipWithReadableJson()
    {
        string path = PathFor("b.uskfx");
        ProjectFile.Save(path, NonDefault());

        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("project.json")!.Open());
        string json = reader.ReadToEnd();

        Assert.Contains($"\"formatVersion\": {ProjectFile.CurrentFormatVersion}", json);
        Assert.Contains("\"Type\": \"Simplex\"", json); // 列挙は名前で保存 (手で読める・並べ替えに強い)
    }

    [Fact]
    public void Image_IsSavedInsideTheProject_AndRestored()
    {
        var image = new ImageData(2, 2, [255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 255, 10, 20, 30, 40]);
        string path = PathFor("image.uskfx");

        ProjectFile.Save(path, new ProjectDocument
        {
            Image = image,
            Source = new SourceSettings { Kind = SourceKind.Image, ImageName = "a.png" },
        });
        var loaded = ProjectFile.Load(path);

        Assert.Equal(SourceKind.Image, loaded.Source.Kind);
        Assert.Equal("a.png", loaded.Source.ImageName);
        Assert.Equal(image.Rgba, loaded.Image!.Rgba);
    }

    [Fact]
    public void OldVersionOneFile_StillOpens()
    {
        string path = PathFor("old.uskfx");
        WriteRawProject(path, """{ "formatVersion": 1, "Preview": { "Mode": "Tile3x3" } }""");

        var loaded = ProjectFile.Load(path);

        Assert.Equal(PreviewMode.Tile3x3, loaded.Preview.Mode);
        Assert.Null(loaded.Image);
    }

    [Fact]
    public void MissingFields_FallBackToDefaults()
    {
        string path = PathFor("partial.uskfx");
        WriteRawProject(path, """{ "formatVersion": 1, "Noise": { "Seed": 99 } }""");

        var loaded = ProjectFile.Load(path);

        Assert.Equal(99u, loaded.Noise.Seed);
        Assert.Equal(new NoiseSettings().Frequency, loaded.Noise.Frequency);
        Assert.Equal(new EffectSettings(), loaded.Effect);
    }

    [Fact]
    public void NewerFormatVersion_IsRejected()
    {
        string path = PathFor("future.uskfx");
        WriteRawProject(path, """{ "formatVersion": 99 }""");

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectFile.Load(path));
        Assert.Contains("99", ex.Message);
    }

    [Fact]
    public void NonZipFile_IsRejectedWithFormatError()
    {
        string path = PathFor("broken.uskfx");
        File.WriteAllText(path, "not a zip");

        Assert.Throws<ProjectFormatException>(() => ProjectFile.Load(path));
    }

    private static void WriteRawProject(string path, string json)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("project.json").Open());
        writer.Write(json);
    }
}
