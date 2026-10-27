using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class PokemonBattleTablesTests(PlatinumFixture fixture)
{
    [Fact]
    public void Shadows_LoadKnownSpecies()
    {
        TestProject t = fixture.CreateProject();
        Assert.True(PokemonShadowService.IsSupported(t.Project, t.Metadata));
        PokemonShadows shadows = PokemonShadowService.Load(t.Project);
        Assert.Equal(494, shadows.Species.Count);
        Assert.Equal(ShadowSize.None, shadows.Species[50].Size);
        Assert.Equal(ShadowSize.None, shadows.Species[51].Size);
        Assert.Equal(ShadowSize.Small, shadows.Species[1].Size);
        Assert.Equal(ShadowSize.Large, shadows.Species[3].Size);
        Assert.Contains(shadows.Species, entry => entry.SpriteOffsetY < 0);
        Assert.Contains(shadows.Species, entry => entry.ShadowOffsetX < 0);
    }

    [Fact]
    public void Shadows_UnchangedSaveKeepsFiles_AndEditPersists()
    {
        TestProject t = fixture.CreateProject();
        string[] paths = [PokemonShadowService.SizePath(t.Project), PokemonShadowService.ShadowOffsetPath(t.Project), PokemonShadowService.SpriteOffsetPath(t.Project)];
        byte[][] before = paths.Select(File.ReadAllBytes).ToArray();
        PokemonShadows shadows = PokemonShadowService.Load(t.Project);
        PokemonShadowService.Save(t.Project, shadows);
        for (int i = 0; i < paths.Length; i++) Assert.Equal(before[i], File.ReadAllBytes(paths[i]));

        shadows.Species[25].Size = ShadowSize.Large;
        shadows.Species[25].ShadowOffsetX = -7;
        shadows.Species[25].SpriteOffsetY = 12;
        PokemonShadowService.Save(t.Project, shadows);
        PokemonShadows after = PokemonShadowService.Load(t.Project);
        Assert.Equal((ShadowSize.Large, (sbyte)-7, (sbyte)12), (after.Species[25].Size, after.Species[25].ShadowOffsetX, after.Species[25].SpriteOffsetY));
        for (int species = 0; species < 494; species++)
        {
            if (species == 25) continue;
            Assert.Equal((shadows.Species[species].Size, shadows.Species[species].ShadowOffsetX, shadows.Species[species].SpriteOffsetY), (after.Species[species].Size, after.Species[species].ShadowOffsetX, after.Species[species].SpriteOffsetY));
        }

        shadows.Species[3].Size = (ShadowSize)9;
        Assert.Throws<InvalidDataException>(() => PokemonShadowService.Save(t.Project, shadows));
    }

    [Fact]
    public void Areas_LoadAndNameKnownPlaces()
    {
        TestProject t = fixture.CreateProject();
        Assert.True(PokedexAreaService.IsSupported(t.Project, t.Metadata));
        PokedexAreas areas = PokedexAreaService.Load(t.Project);
        Assert.Equal(494, areas.Species.Count);
        Assert.All(areas.Species, species => Assert.Equal(PokedexAreaService.CategoryCount, species.Lists.Count));

        IReadOnlyList<string> special = PokedexAreaService.PlaceNames(t.Project, t.Metadata, true), routes = PokedexAreaService.PlaceNames(t.Project, t.Metadata, false);
        Assert.Equal(PokedexAreaService.VanillaSpecialPlaceCount, special.Count);
        Assert.Equal(PokedexAreaService.VanillaRoutePlaceCount, routes.Count);
        Assert.Equal("Oreburgh Mine", special[0]);
        Assert.Equal("Great Marsh", special[3]);
        Assert.Equal("Floaroma Meadow", special[20]);
        Assert.Equal("Route 201", routes[11]);

        // Bidoof lives on Route 201 morning, day and night; the Honey Tree species are listed in the Honey Tree categories.
        Assert.All(new[] { 5, 6, 7 }, category => Assert.Contains(12, areas.Species[399].Lists[category].Areas));
        Assert.All(new[] { 190, 214, 265, 412, 415, 420 }, species => Assert.NotEmpty(areas.Species[species].Lists[8].Areas));
        Assert.Empty(areas.Species[399].Lists[8].Areas);
    }

    [Fact]
    public void Areas_UnchangedSaveKeepsArchive_AndEditsPersist()
    {
        TestProject t = fixture.CreateProject();
        string path = PokedexAreaService.ArchivePath(t.Project);
        byte[] before = File.ReadAllBytes(path);
        PokedexAreas areas = PokedexAreaService.Load(t.Project);
        PokedexAreaService.Save(t.Project, areas);
        Assert.Equal(before, File.ReadAllBytes(path));

        List<int> morning = areas.Species[399].Lists[5].Areas;
        int removed = morning[0];
        morning.RemoveAt(0);
        areas.Species[25].Lists[6].Areas = [3, 9, 40];
        areas.Species[493].Lists[2].Areas = [1, 21];
        PokedexAreaService.Save(t.Project, areas);

        PokedexAreas after = PokedexAreaService.Load(t.Project);
        Assert.DoesNotContain(removed, after.Species[399].Lists[5].Areas);
        Assert.Equal([3, 9, 40], after.Species[25].Lists[6].Areas);
        Assert.Equal([1, 21], after.Species[493].Lists[2].Areas);
        for (int species = 1; species < 494; species++)
        {
            for (int category = 0; category < 10; category++)
            {
                if ((species, category) is (399, 5) or (25, 6) or (493, 2)) continue;
                Assert.Equal(areas.Species[species].Lists[category].Areas, after.Species[species].Lists[category].Areas);
            }
        }

        areas.Species[25].Lists[0].Areas = [22];
        Assert.Throws<InvalidDataException>(() => PokedexAreaService.Save(t.Project, areas));
        areas.Species[25].Lists[0].Areas = [];
        areas.Species[25].Lists[5].Areas = [51];
        Assert.Throws<InvalidDataException>(() => PokedexAreaService.Save(t.Project, areas));
    }

    [Fact]
    public async Task Edits_ReplayFromTheJournalAndSurviveARomBuild()
    {
        TestProject a = fixture.CreateProject(), b = fixture.CreateProject();
        string journal = Path.Combine(a.Project.RootPath, EditJournal.FileName);
        EditJournal.Open(a.Project, a.Metadata, journalPath: journal);
        try
        {
            PokemonShadows shadows = PokemonShadowService.Load(a.Project);
            shadows.Species[133].Size = ShadowSize.Small;
            shadows.Species[133].SpriteOffsetY = -4;
            PokemonShadowService.Save(a.Project, shadows);
            PokedexAreas areas = PokedexAreaService.Load(a.Project);
            areas.Species[399].Lists[7].Areas.Add(44);
            areas.Species[133].Lists[9].Areas = [5, 6];
            PokedexAreaService.Save(a.Project, areas);
        }
        finally
        {
            EditJournal.Close();
        }

        string[] lines = File.ReadAllLines(journal);
        Assert.Contains(lines, line => line.Contains("SetPokemonShadows") || line.Contains("PokemonShadows"));
        Assert.Contains(lines, line => line.Contains("PokedexAreas"));
        Assert.True(EditJournalReplayer.Replay(b.Project, b.Metadata, journal) > 0);
        Assert.Equal((ShadowSize.Small, (sbyte)-4), (PokemonShadowService.Load(b.Project).Species[133].Size, PokemonShadowService.Load(b.Project).Species[133].SpriteOffsetY));
        PokedexAreas replayed = PokedexAreaService.Load(b.Project);
        Assert.Contains(44, replayed.Species[399].Lists[7].Areas);
        Assert.Equal([5, 6], replayed.Species[133].Lists[9].Areas);

        var service = new RomExtractionService();
        string rom = Path.Combine(a.Project.RootPath, "..", "pokebattle-" + Guid.NewGuid().ToString("N") + ".nds");
        Assert.True((await service.BuildAsync(a.Project.RootPath, rom, a.ToolsDirectory)).Succeeded);
        string extracted = Path.Combine(Path.GetDirectoryName(rom)!, "pokebattle-reextract-" + Guid.NewGuid().ToString("N"));
        Assert.True((await service.ExtractAsync(rom, extracted, RomProjectFormat.DsRom, a.ToolsDirectory)).Succeeded);
        RomProject.TryOpen(extracted, out RomProject? reopened);
        Assert.Equal(-4, PokemonShadowService.Load(reopened!).Species[133].SpriteOffsetY);
        Assert.Equal([5, 6], PokedexAreaService.Load(reopened!).Species[133].Lists[9].Areas);
        Assert.Contains(44, PokedexAreaService.Load(reopened!).Species[399].Lists[7].Areas);
    }
}
