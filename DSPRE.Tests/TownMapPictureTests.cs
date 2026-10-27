using System.Buffers.Binary;
using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class TownMapPictureTests(PlatinumFixture fixture)
{
    private static string Archive(RomProject p) => Path.Combine(p.FilesPath, "graphic", "tmap_gra.narc");

    private static HashSet<int> TilesOf(string path, int screenMember)
    {
        byte[] data = NarcFile.ReadMember(path, screenMember);
        var tiles = new HashSet<int>();
        for (int at = 0x24; at + 2 <= data.Length; at += 2) tiles.Add(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) & 0x3FF);
        return tiles;
    }

    [Fact]
    public void Load_MatchesTheRenderedPicture()
    {
        TestProject t = fixture.CreateProject();
        Assert.True(TownMapPictureService.IsSupported(t.Project, t.Metadata));
        TownMapPicture picture = TownMapPictureService.Load(t.Project);
        Assert.Equal(512, picture.Rows.Count);
        Assert.All(picture.Rows, row => Assert.Equal(512, row.Length));
        RgbColor[] palette = TownMapPictureService.Palette(t.Project);
        Assert.Equal(16, palette.Length);
        Assert.Equal(PokedexMapService.RenderTownMap(t.Project), TownMapPictureService.ToRgba(picture, palette));
        Assert.Contains(picture.Rows, row => row.Any(c => c != '0'));
    }

    [Fact]
    public void UnchangedSaveKeepsTheArchive_AndRgbaRoundTrips()
    {
        TestProject t = fixture.CreateProject();
        byte[] before = File.ReadAllBytes(Archive(t.Project));
        TownMapPicture picture = TownMapPictureService.Load(t.Project);
        TownMapPictureService.Save(t.Project, picture);
        Assert.Equal(before, File.ReadAllBytes(Archive(t.Project)));

        RgbColor[] palette = TownMapPictureService.Palette(t.Project);
        TownMapPicture again = TownMapPictureService.FromRgba(TownMapPictureService.ToRgba(picture, palette), 512, 512, palette);
        Assert.Equal(picture.Rows, again.Rows);
        Assert.Throws<InvalidDataException>(() => TownMapPictureService.FromRgba(new byte[16 * 16 * 4], 16, 16, palette));
    }

    [Fact]
    public void EditsPersist_AndTilesOtherScreensUseAreNeverChanged()
    {
        TestProject t = fixture.CreateProject();
        string path = Archive(t.Project);
        byte[] sheetBefore = NarcFile.ReadMember(path, 20);
        var others = new HashSet<int>();
        foreach (int screen in new[] { 22, 23, 24, 25, 27, 28, 29 }) others.UnionWith(TilesOf(path, screen));
        byte[][] otherScreensBefore = new[] { 22, 23, 24, 25, 27, 28, 29 }.Select(m => NarcFile.ReadMember(path, m)).ToArray();

        TownMapPicture picture = TownMapPictureService.Load(t.Project);
        int color = picture.Rows.SelectMany(row => row).First(c => c != '0') is char c0 ? Convert.ToInt32(c0.ToString(), 16) : 1;
        for (int y = 20; y < 52; y++)
        {
            for (int x = 20; x < 44; x++) picture.Set(x, y, (x + y) % 3 == 0 ? color : 0);
        }

        picture.Set(300, 301, color);
        TownMapPictureService.Save(t.Project, picture);

        TownMapPicture after = TownMapPictureService.Load(t.Project);
        Assert.Equal(picture.Rows, after.Rows);
        byte[] sheetAfter = NarcFile.ReadMember(path, 20);
        foreach (int tile in others) Assert.Equal(sheetBefore.AsSpan(0x30 + tile * 32, 32).ToArray(), sheetAfter.AsSpan(0x30 + tile * 32, 32).ToArray());
        Assert.Equal(sheetBefore.Length, sheetAfter.Length);
        for (int i = 0; i < otherScreensBefore.Length; i++) Assert.Equal(otherScreensBefore[i], NarcFile.ReadMember(path, new[] { 22, 23, 24, 25, 27, 28, 29 }[i]));
    }

    [Fact]
    public void APictureNeedingTooManyTilesIsRefusedAndNothingChanges()
    {
        TestProject t = fixture.CreateProject();
        byte[] before = File.ReadAllBytes(Archive(t.Project));
        TownMapPicture picture = TownMapPictureService.Load(t.Project);
        var random = new Random(7);
        for (int y = 0; y < 512; y++) picture.Rows[y] = new string(Enumerable.Range(0, 512).Select(_ => "0123456789ABCDEF"[random.Next(16)]).ToArray());
        Assert.Throws<InvalidDataException>(() => TownMapPictureService.Save(t.Project, picture));
        Assert.Equal(before, File.ReadAllBytes(Archive(t.Project)));
        picture.Rows[3] = "short";
        Assert.Throws<InvalidDataException>(() => TownMapPictureService.Save(t.Project, picture));
    }

    [Fact]
    public async Task EditsReplayFromTheJournalAndSurviveARomBuild()
    {
        TestProject a = fixture.CreateProject(), b = fixture.CreateProject();
        string journal = Path.Combine(a.Project.RootPath, EditJournal.FileName);
        EditJournal.Open(a.Project, a.Metadata, journalPath: journal);
        try
        {
            TownMapPicture picture = TownMapPictureService.Load(a.Project);
            for (int x = 10; x < 30; x++) picture.Set(x, 10, 5);
            TownMapPictureService.Save(a.Project, picture);
        }
        finally
        {
            EditJournal.Close();
        }

        Assert.Contains(File.ReadAllLines(journal), line => line.Contains("TownMapPicture"));
        Assert.True(EditJournalReplayer.Replay(b.Project, b.Metadata, journal) > 0);
        Assert.Equal(TownMapPictureService.Load(a.Project).Rows, TownMapPictureService.Load(b.Project).Rows);

        var service = new RomExtractionService();
        string rom = Path.Combine(a.Project.RootPath, "..", "townmap-" + Guid.NewGuid().ToString("N") + ".nds");
        Assert.True((await service.BuildAsync(a.Project.RootPath, rom, a.ToolsDirectory)).Succeeded);
        string extracted = Path.Combine(Path.GetDirectoryName(rom)!, "townmap-reextract-" + Guid.NewGuid().ToString("N"));
        Assert.True((await service.ExtractAsync(rom, extracted, RomProjectFormat.DsRom, a.ToolsDirectory)).Succeeded);
        RomProject.TryOpen(extracted, out RomProject? reopened);
        Assert.Equal(TownMapPictureService.Load(a.Project).Rows, TownMapPictureService.Load(reopened!).Rows);
    }

    [Fact]
    public void TileBudget_SaysWhetherAPictureWillFit()
    {
        TestProject t = fixture.CreateProject();
        TownMapPicture picture = TownMapPictureService.Load(t.Project);
        (int needed, int free) = TownMapPictureService.TileBudget(t.Project, picture);
        Assert.Equal(0, needed);
        Assert.InRange(free, 60, 120);

        picture.Set(100, 100, 5);
        (int small, int sameFree) = TownMapPictureService.TileBudget(t.Project, picture);
        Assert.InRange(small, 1, sameFree);
        Assert.InRange(sameFree, free, free + 1);

        var random = new Random(3);
        for (int y = 0; y < 512; y++) picture.Rows[y] = new string(Enumerable.Range(0, 512).Select(_ => "0123456789ABCDEF"[random.Next(16)]).ToArray());
        (int huge, int hugeFree) = TownMapPictureService.TileBudget(t.Project, picture);
        Assert.True(huge > hugeFree, $"{huge} should be more than the {hugeFree} free tiles");
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => TownMapPictureService.Save(t.Project, picture));
        Assert.Contains($"{huge}", error.Message);
        Assert.Contains($"{hugeFree}", error.Message);

        // Going back to the saved picture is always possible: it needs nothing.
        TownMapPicture original = TownMapPictureService.Load(t.Project);
        Assert.Equal((0, free), TownMapPictureService.TileBudget(t.Project, original));
        TownMapPictureService.Save(t.Project, original);
    }

    [Theory]
    [InlineData(TownMapView.Routes)]
    [InlineData(TownMapView.Terrain)]
    public void EachView_LoadsSavesAndLeavesTheOtherPictureAlone(TownMapView view)
    {
        TestProject t = fixture.CreateProject();
        string path = Archive(t.Project);
        TownMapView other = view == TownMapView.Terrain ? TownMapView.Routes : TownMapView.Terrain;
        TownMapPicture picture = TownMapPictureService.Load(t.Project, view);
        Assert.Equal(512, picture.Rows.Count);
        Assert.Contains(picture.Rows, row => row.Any(c => c != '0'));
        RgbColor[] palette = TownMapPictureService.Palette(t.Project, view);
        Assert.Equal(16, palette.Length);

        byte[] before = File.ReadAllBytes(path);
        TownMapPictureService.Save(t.Project, picture, view);
        Assert.Equal(before, File.ReadAllBytes(path));

        TownMapPicture otherBefore = TownMapPictureService.Load(t.Project, other);
        int color = picture.Rows.SelectMany(row => row).First(c => c != '0') is char c0 ? Convert.ToInt32(c0.ToString(), 16) : 1;
        for (int y = 30; y < 62; y++)
        {
            for (int x = 40; x < 70; x++) picture.Set(x, y, (x * 3 + y) % 5 == 0 ? color : 0);
        }

        (int needed, int free) = TownMapPictureService.TileBudget(t.Project, picture, view);
        Assert.True(needed <= free, $"{needed} > {free}");
        TownMapPictureService.Save(t.Project, picture, view);
        Assert.Equal(picture.Rows, TownMapPictureService.Load(t.Project, view).Rows);
        Assert.Equal(otherBefore.Rows, TownMapPictureService.Load(t.Project, other).Rows);
        foreach (int screen in new[] { 22, 23, 24, 27, 28, 29 }) Assert.Equal(TilesOf(path, screen), TilesOf(path, screen));
        // Everything but the screen and tile sheet is byte for byte as it was.
        for (int member = 0; member < NarcFile.MemberCount(path); member++)
        {
            if (member == 20 || member == (view == TownMapView.Terrain ? 25 : 26)) continue;
            Assert.Equal(NarcFile.ReadMember(Path.Combine(t.Project.FilesPath, "graphic", "tmap_gra.narc"), member), NarcFile.ReadMember(path, member));
        }
    }

    [Fact]
    public void TheTerrainPicture_ReplaysFromTheJournal()
    {
        TestProject a = fixture.CreateProject(), b = fixture.CreateProject();
        string journal = Path.Combine(a.Project.RootPath, EditJournal.FileName);
        EditJournal.Open(a.Project, a.Metadata, journalPath: journal);
        try
        {
            TownMapPicture picture = TownMapPictureService.Load(a.Project, TownMapView.Terrain);
            for (int x = 50; x < 90; x++) picture.Set(x, 50, picture.Get(x, 50) == 1 ? 2 : 1);
            TownMapPictureService.Save(a.Project, picture, TownMapView.Terrain);
        }
        finally
        {
            EditJournal.Close();
        }

        Assert.Contains(File.ReadAllLines(journal), line => line.StartsWith("SetTownMapPicture(") && line.Contains("view: \"Terrain\""));
        Assert.True(EditJournalReplayer.Replay(b.Project, b.Metadata, journal) > 0);
        Assert.Equal(TownMapPictureService.Load(a.Project, TownMapView.Terrain).Rows, TownMapPictureService.Load(b.Project, TownMapView.Terrain).Rows);
        Assert.Equal(TownMapPictureService.Load(a.Project).Rows, TownMapPictureService.Load(b.Project).Rows);
    }
}
