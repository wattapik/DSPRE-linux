using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class FootprintTests(PlatinumFixture fixture)
{
    [Fact]
    public void Load_ReadsEverySpeciesAndKnownBlanks()
    {
        TestProject t = fixture.CreateProject();
        Assert.True(FootprintService.IsSupported(t.Project, t.Metadata));
        FootprintSet set = FootprintService.Load(t.Project);
        Assert.Equal(494, set.Footprints.Count);
        Assert.All(set.Footprints, footprint =>
        {
            Assert.Equal(16, footprint.Rows.Count);
            Assert.All(footprint.Rows, row => Assert.Equal(16, row.Length));
        });

        bool Blank(int species) => set.Footprints[species].Rows.All(row => row.All(c => c == '.'));
        Assert.All(new[] { 11, 14, 23, 24, 41, 50, 92, 100, 201 }, species => Assert.True(Blank(species), $"species {species} has no feet"));
        Assert.All(new[] { 1, 4, 7, 25, 150, 249 }, species => Assert.False(Blank(species), $"species {species} has a footprint"));
        Assert.Equal(108, Enumerable.Range(0, 494).Count(Blank));
    }

    [Fact]
    public void UnchangedSaveLeavesTheArchiveIdentical()
    {
        TestProject t = fixture.CreateProject();
        byte[] before = File.ReadAllBytes(FootprintService.ArchivePath(t.Project));
        FootprintService.Save(t.Project, FootprintService.Load(t.Project));
        Assert.Equal(before, File.ReadAllBytes(FootprintService.ArchivePath(t.Project)));
    }

    [Fact]
    public void EditPersists_AndOnlyTouchesThatSpeciesAndItsShownTiles()
    {
        TestProject t = fixture.CreateProject();
        string path = FootprintService.ArchivePath(t.Project);
        FootprintSet set = FootprintService.Load(t.Project);
        byte[] otherBefore = NarcFile.ReadMember(path, 25 + 3);
        byte[] fillBefore = Lz10.Decompress(NarcFile.ReadMember(path, 23 + 3));

        char[][] rows = set.Footprints[23].Rows.Select(row => row.ToCharArray()).ToArray();
        rows[0][0] = 'k';
        rows[7][15] = 'k';
        rows[8][8] = 'k';
        rows[15][15] = 'k';
        set.Footprints[23].Rows = rows.Select(row => new string(row)).ToList();
        FootprintService.Save(t.Project, set);

        FootprintSet after = FootprintService.Load(t.Project);
        Assert.Equal(set.Footprints[23].Rows, after.Footprints[23].Rows);
        Assert.Equal('k', after.Footprints[23].Rows[0][0]);
        Assert.Equal('k', after.Footprints[23].Rows[15][15]);
        Assert.Equal(otherBefore, NarcFile.ReadMember(path, 25 + 3));
        Assert.Equal(494, after.Footprints.Count);

        byte[] fillAfter = Lz10.Decompress(NarcFile.ReadMember(path, 23 + 3));
        foreach (int tile in new[] { 2, 3, 6, 7 })
        {
            Assert.Equal(fillBefore.AsSpan(0x30 + tile * 32, 32).ToArray(), fillAfter.AsSpan(0x30 + tile * 32, 32).ToArray());
        }

        Assert.Equal(fillBefore.AsSpan(0, 0x30).ToArray(), fillAfter.AsSpan(0, 0x30).ToArray());
    }

    [Fact]
    public void Rgba_RoundTripsAndRejectsOtherSizes()
    {
        TestProject t = fixture.CreateProject();
        RgbColor black = FootprintService.Color(t.Project);
        Assert.Equal(new RgbColor(0, 0, 0), black);
        foreach (Footprint footprint in FootprintService.Load(t.Project).Footprints.Take(60))
        {
            Footprint again = FootprintService.FromRgba(FootprintService.ToRgba(footprint, black), 16, 16);
            Assert.Equal(footprint.Rows, again.Rows);
        }

        Assert.Throws<InvalidDataException>(() => FootprintService.FromRgba(new byte[32 * 16 * 4], 32, 16));
    }

    [Fact]
    public void EditsKeepEachSpeciesOwnInk()
    {
        TestProject t = fixture.CreateProject();
        string path = FootprintService.ArchivePath(t.Project);
        FootprintSet set = FootprintService.Load(t.Project);
        // 255 (Torchic line) draws with palette entry 1, 25 (Pikachu line) with entry 4; blank 23 and 255-range blanks follow their neighbours.
        int Ink(int species) => Lz10.Decompress(NarcFile.ReadMember(path, species + 3)).Skip(0x30).Take(0x100).SelectMany(b => new[] { b & 15, b >> 4 }).First(v => v is 1 or 4);
        Assert.Equal(1, Ink(255));
        Assert.Equal(4, Ink(25));
        foreach (int species in new[] { 255, 25 })
        {
            set.Footprints[species].Rows[4] = "kkkkkkkkkkkkkkkk";
        }

        FootprintService.Save(t.Project, set);
        Assert.Equal(1, Ink(255));
        Assert.Equal(4, Ink(25));
        Assert.All(Lz10.Decompress(NarcFile.ReadMember(path, 255 + 3)).Skip(0x30).Take(0x100).Select((b, i) => (b, i)).Where(p => p.i < 64 || (p.i >= 128 && p.i < 192)), p => Assert.DoesNotContain(4, new[] { p.b & 15, p.b >> 4 }));
    }

    [Fact]
    public void SaveRejectsDamagedRows()
    {
        TestProject t = fixture.CreateProject();
        FootprintSet set = FootprintService.Load(t.Project);
        set.Footprints[5].Rows[3] = "short";
        Assert.Throws<InvalidDataException>(() => FootprintService.Save(t.Project, set));
    }

    [Fact]
    public async Task EditsReplayFromTheJournalAndSurviveARomBuild()
    {
        TestProject a = fixture.CreateProject(), b = fixture.CreateProject();
        string journal = Path.Combine(a.Project.RootPath, EditJournal.FileName);
        EditJournal.Open(a.Project, a.Metadata, journalPath: journal);
        try
        {
            FootprintSet set = FootprintService.Load(a.Project);
            char[] row = set.Footprints[41].Rows[9].ToCharArray();
            row[4] = 'k';
            set.Footprints[41].Rows[9] = new string(row);
            FootprintService.Save(a.Project, set);
        }
        finally
        {
            EditJournal.Close();
        }

        Assert.Contains(File.ReadAllLines(journal), line => line.Contains("Footprints[41]"));
        Assert.True(EditJournalReplayer.Replay(b.Project, b.Metadata, journal) > 0);
        Assert.Equal('k', FootprintService.Load(b.Project).Footprints[41].Rows[9][4]);

        var service = new RomExtractionService();
        string rom = Path.Combine(a.Project.RootPath, "..", "footprint-" + Guid.NewGuid().ToString("N") + ".nds");
        Assert.True((await service.BuildAsync(a.Project.RootPath, rom, a.ToolsDirectory)).Succeeded);
        string extracted = Path.Combine(Path.GetDirectoryName(rom)!, "footprint-reextract-" + Guid.NewGuid().ToString("N"));
        Assert.True((await service.ExtractAsync(rom, extracted, RomProjectFormat.DsRom, a.ToolsDirectory)).Succeeded);
        RomProject.TryOpen(extracted, out RomProject? reopened);
        Assert.Equal('k', FootprintService.Load(reopened!).Footprints[41].Rows[9][4]);
    }
}
