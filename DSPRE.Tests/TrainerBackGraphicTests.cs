using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class TrainerBackGraphicTests(PlatinumFixture fixture)
{
    private const TrainerGraphicSide Back = TrainerGraphicSide.Back;

    private static string WriteTga(string path, byte[] rgba, int width, int height)
    {
        var file = new byte[18 + rgba.Length];
        file[2] = 2;
        file[12] = (byte)width;
        file[13] = (byte)(width >> 8);
        file[14] = (byte)height;
        file[15] = (byte)(height >> 8);
        file[16] = 32;
        file[17] = 0x28;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            file[18 + i] = rgba[i + 2];
            file[18 + i + 1] = rgba[i + 1];
            file[18 + i + 2] = rgba[i];
            file[18 + i + 3] = rgba[i + 3];
        }

        File.WriteAllBytes(path, file);
        return path;
    }

    [Fact]
    public void Back_EveryGroupLoadsAndRenders()
    {
        TestProject t = fixture.CreateProject();
        new RomExtractionService().PrepareSprites(t.Project, t.Metadata);
        Assert.True(TrainerGraphicService.IsSupported(t.Metadata, Back));
        int count = TrainerGraphicService.ClassCount(t.Project, Back);
        Assert.Equal(11, count);
        for (int i = 0; i < count; i++)
        {
            Assert.True(TrainerGraphicService.FrameCount(t.Project, i, Back) > 0);
            byte[] sheet = TrainerGraphicService.RenderSheet(t.Project, i, Back);
            Assert.Contains(sheet, b => b != 0);
            Assert.True(SpriteService.TryLoadTrainerClassSprite(t.Project, t.Metadata, i, out _, out _, out _, out _, Back));
            TrainerAnimationService.Load(t.Project, i, Back);
        }
    }

    [Fact]
    public void Back_ReimportingTheRenderedSheetKeepsThePictures()
    {
        TestProject t = fixture.CreateProject();
        new RomExtractionService().PrepareSprites(t.Project, t.Metadata);
        string temp = Path.Combine(Path.GetTempPath(), "back-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        for (int i = 0; i < TrainerGraphicService.ClassCount(t.Project, Back); i++)
        {
            string scanPath = Path.Combine(t.Project.TrainerBackGraphicsDirectory, (i * 5 + 4).ToString("D4"));
            byte[] sheetBefore = TrainerGraphicService.RenderSheet(t.Project, i, Back);
            byte[] scanBefore = BattleSpriteService.DecodeSpriteIndices(File.ReadAllBytes(scanPath))!;
            int frames = TrainerGraphicService.FrameCount(t.Project, i, Back);
            string tga = WriteTga(Path.Combine(temp, $"{i}.tga"), sheetBefore, 80, 80 * frames);
            TrainerGraphicService.ImportResult result = TrainerGraphicService.Import(t.Project, t.Metadata, i, tga, Back);
            Assert.Equal(frames, result.Frames);
            Assert.True(result.ScanUpdated);
            Assert.Equal(sheetBefore, TrainerGraphicService.RenderSheet(t.Project, i, Back));
            byte[] scanAfter = BattleSpriteService.DecodeSpriteIndices(File.ReadAllBytes(scanPath))!;
            Assert.Equal(scanBefore.Select(b => b != 0), scanAfter.Select(b => b != 0));
        }
    }

    [Fact]
    public void Back_ChangedFrameReachesTilesAndScan()
    {
        TestProject t = fixture.CreateProject();
        new RomExtractionService().PrepareSprites(t.Project, t.Metadata);
        byte[] sheet = TrainerGraphicService.RenderSheet(t.Project, 3, Back);
        int frames = TrainerGraphicService.FrameCount(t.Project, 3, Back);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) { int at = (y * 80 + x) * 4; sheet[at] = 255; sheet[at + 1] = sheet[at + 2] = 0; sheet[at + 3] = 255; }
        string tga = WriteTga(Path.Combine(Path.GetTempPath(), "back-edit-" + Guid.NewGuid().ToString("N") + ".tga"), sheet, 80, 80 * frames);
        TrainerGraphicService.Import(t.Project, t.Metadata, 3, tga, Back);

        byte[] after = TrainerGraphicService.RenderSheet(t.Project, 3, Back);
        Assert.Equal(248, after[0]);
        Assert.Equal(0, after[1]);
        byte[] scan = BattleSpriteService.DecodeSpriteIndices(File.ReadAllBytes(Path.Combine(t.Project.TrainerBackGraphicsDirectory, "0019")))!;
        Assert.NotEqual(0, scan[5 * 160 + 5]);
    }
}
