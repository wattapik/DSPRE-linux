using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class PokedexMapTests(PlatinumFixture fixture)
{
    [Fact]
    public void Load_ReadsEveryPlace()
    {
        TestProject t = fixture.CreateProject();
        Assert.True(PokedexMapService.IsSupported(t.Project, t.Metadata));
        PokedexAreaMap map = PokedexMapService.Load(t.Project);
        Assert.Equal(21, map.Specials.Count);
        Assert.Equal(50, map.Routes.Count);
        Assert.Equal(1, map.Specials[2].Flag);
        Assert.Equal(207u, map.Specials[2].Header);
        Assert.Equal(1, map.Specials.Count(place => place.Flag == 1));
        Assert.Equal(198u, map.Specials[0].Header);
        Assert.Equal((9, 24), (map.Specials[0].X, map.Specials[0].Y));
        Assert.Equal(33u, map.Routes[0].Header);
        RoutePlace pastoria = map.Routes[2];
        Assert.Equal((18, 25, 2, 2), (pastoria.X, pastoria.Y, pastoria.Width, pastoria.Height));
        Assert.Equal(["##", "##"], pastoria.Rows);
        Assert.Contains(map.Routes, route => route.Rows.Any(row => row.Contains('.')));
        Assert.All(map.Routes, route => Assert.True(route.Width * route.Height <= PokedexMapService.MaxCells));
    }

    [Fact]
    public void UnchangedSaveKeepsTheArchive_AndEditsPersist()
    {
        TestProject t = fixture.CreateProject();
        string path = PokedexAreaService.ArchivePath(t.Project);
        byte[] before = File.ReadAllBytes(path);
        PokedexAreaMap map = PokedexMapService.Load(t.Project);
        PokedexMapService.Save(t.Project, map);
        Assert.Equal(before, File.ReadAllBytes(path));

        map.Routes[11].SetCell(3, 25, true);
        map.Routes[11].Header = 400;
        map.Specials[4].X = 20;
        map.Specials[4].Y = 10;
        map.Specials[4].Header = 301;
        PokedexMapService.Save(t.Project, map);
        PokedexAreaMap after = PokedexMapService.Load(t.Project);
        Assert.Equal(map.Routes[11].Rows, after.Routes[11].Rows);
        Assert.Equal((3, 25, 2, 2, 400u), (after.Routes[11].X, after.Routes[11].Y, after.Routes[11].Width, after.Routes[11].Height, after.Routes[11].Header));
        Assert.Equal((20, 10, 301u), (after.Specials[4].X, after.Specials[4].Y, after.Specials[4].Header));
        for (int i = 0; i < 50; i++)
        {
            if (i == 11) continue;
            Assert.Equal((map.Routes[i].X, map.Routes[i].Y, string.Join('/', map.Routes[i].Rows), map.Routes[i].Header), (after.Routes[i].X, after.Routes[i].Y, string.Join('/', after.Routes[i].Rows), after.Routes[i].Header));
        }

        // The species lists, which share the archive, are untouched by a map edit.
        Assert.Equal(PokedexAreaService.Load(t.Project).Species[399].Lists[5].Areas, PokedexAreaService.Load(t.Project).Species[399].Lists[5].Areas);
    }

    [Fact]
    public void SetCell_GrowsShrinksAndRefusesTooBigBoxes()
    {
        var place = new RoutePlace { X = 5, Y = 5, Width = 2, Height = 1, Rows = ["##"] };
        Assert.True(place.SetCell(6, 6, true));
        Assert.Equal((5, 5, 2, 2), (place.X, place.Y, place.Width, place.Height));
        Assert.Equal(["##", ".#"], place.Rows);
        Assert.True(place.SetCell(5, 5, false));
        Assert.Equal((6, 5, 1, 2), (place.X, place.Y, place.Width, place.Height));
        Assert.Equal(["#", "#"], place.Rows);
        Assert.False(place.SetCell(6, 5 + 32, true));
        Assert.Equal((6, 5, 1, 2), (place.X, place.Y, place.Width, place.Height));
        Assert.True(place.SetCell(6, 5, false));
        Assert.True(place.SetCell(6, 6, false));
        Assert.Equal((1, 1, "."), (place.Width, place.Height, string.Join('/', place.Rows)));
        Assert.True(place.MoveBy(2, 3));
        Assert.False(place.MoveBy(-1000, 0));
    }

    [Fact]
    public void TheTownMapPicture_HasEveryAreaCellOnIt()
    {
        TestProject t = fixture.CreateProject();
        byte[] picture = PokedexMapService.RenderTownMap(t.Project);
        Assert.Equal(PokedexMapService.MapSize * PokedexMapService.MapSize * 4, picture.Length);
        Assert.Contains(picture.Where((_, i) => i % 4 == 3), alpha => alpha == 255);
        PokedexAreaMap map = PokedexMapService.Load(t.Project);
        foreach (RoutePlace route in map.Routes)
        {
            foreach ((int x, int y) in route.Cells())
            {
                int pixel = ((y * PokedexMapService.CellSize + PokedexMapService.OriginY + 8) * PokedexMapService.MapSize + x * PokedexMapService.CellSize + PokedexMapService.OriginX + 8) * 4 + 3;
                Assert.Equal(255, picture[pixel]);
            }
        }
    }

    [Fact]
    public async Task Edits_ReplayFromTheJournalAndSurviveARomBuild()
    {
        TestProject a = fixture.CreateProject(), b = fixture.CreateProject();
        string journal = Path.Combine(a.Project.RootPath, EditJournal.FileName);
        EditJournal.Open(a.Project, a.Metadata, journalPath: journal);
        try
        {
            PokedexAreaMap map = PokedexMapService.Load(a.Project);
            map.Routes[20].SetCell(map.Routes[20].X + map.Routes[20].Width, map.Routes[20].Y, true);
            map.Specials[9].X = 3;
            PokedexMapService.Save(a.Project, map);
        }
        finally
        {
            EditJournal.Close();
        }

        Assert.Contains(File.ReadAllLines(journal), line => line.Contains("PokedexAreaMap"));
        Assert.True(EditJournalReplayer.Replay(b.Project, b.Metadata, journal) > 0);
        PokedexAreaMap original = PokedexMapService.Load(a.Project), replayed = PokedexMapService.Load(b.Project);
        Assert.Equal(original.Routes[20].Rows, replayed.Routes[20].Rows);
        Assert.Equal(3, replayed.Specials[9].X);

        var service = new RomExtractionService();
        string rom = Path.Combine(a.Project.RootPath, "..", "areamap-" + Guid.NewGuid().ToString("N") + ".nds");
        Assert.True((await service.BuildAsync(a.Project.RootPath, rom, a.ToolsDirectory)).Succeeded);
        string extracted = Path.Combine(Path.GetDirectoryName(rom)!, "areamap-reextract-" + Guid.NewGuid().ToString("N"));
        Assert.True((await service.ExtractAsync(rom, extracted, RomProjectFormat.DsRom, a.ToolsDirectory)).Succeeded);
        RomProject.TryOpen(extracted, out RomProject? reopened);
        PokedexAreaMap fromRom = PokedexMapService.Load(reopened!);
        Assert.Equal(original.Routes[20].Rows, fromRom.Routes[20].Rows);
        Assert.Equal(3, fromRom.Specials[9].X);
    }

    [Fact]
    public void NewPlacesCanBeAddedListedAndRemoved()
    {
        TestProject t = fixture.CreateProject();
        string path = PokedexAreaService.ArchivePath(t.Project);
        PokedexAreaMap map = PokedexMapService.Load(t.Project);
        PokedexAreas listsBefore = PokedexAreaService.Load(t.Project);

        var route = new RoutePlace { Header = 342 };
        Assert.True(route.SetCell(5, 5, true));
        Assert.True(route.SetCell(6, 5, true));
        map.Routes.Add(route);
        map.Specials.Add(new SpecialPlace { Header = 198, X = 7, Y = 7 });
        PokedexMapService.Save(t.Project, map);

        PokedexAreaService.PlaceCounts counts = PokedexAreaService.Counts(t.Project);
        Assert.Equal((22, 51), (counts.Specials, counts.Routes));
        PokedexAreaMap after = PokedexMapService.Load(t.Project);
        Assert.Equal((22, 51), (after.Specials.Count, after.Routes.Count));
        Assert.Equal(["##"], after.Routes[50].Rows);
        Assert.Equal((5, 5, 342u), (after.Routes[50].X, after.Routes[50].Y, after.Routes[50].Header));
        Assert.Equal((7, 7, 198u), (after.Specials[21].X, after.Specials[21].Y, after.Specials[21].Header));
        for (int i = 0; i < 50; i++) Assert.Equal(string.Join('/', map.Routes[i].Rows), string.Join('/', after.Routes[i].Rows));

        // The species lists are untouched by the bigger tables, and can now name the new places.
        PokedexAreas listsAfter = PokedexAreaService.Load(t.Project);
        for (int species = 1; species < 494; species++)
        {
            for (int category = 0; category < 10; category++) Assert.Equal(listsBefore.Species[species].Lists[category].Areas, listsAfter.Species[species].Lists[category].Areas);
        }

        listsAfter.Species[399].Lists[5].Areas.Add(51);
        listsAfter.Species[399].Lists[0].Areas.Add(22);
        PokedexAreaService.Save(t.Project, listsAfter);
        PokedexAreas reread = PokedexAreaService.Load(t.Project);
        Assert.Contains(51, reread.Species[399].Lists[5].Areas);
        Assert.Contains(22, reread.Species[399].Lists[0].Areas);

        // A place that a species still lists cannot be removed; once the list drops it, it can.
        map.Routes.RemoveAt(50);
        Assert.Throws<InvalidDataException>(() => PokedexMapService.Save(t.Project, map));
        reread.Species[399].Lists[5].Areas.Remove(51);
        PokedexAreaService.Save(t.Project, reread);
        PokedexMapService.Save(t.Project, map);
        Assert.Equal(50, PokedexAreaService.Counts(t.Project).Routes);
        Assert.Equal(22, PokedexAreaService.Counts(t.Project).Specials);
        map.Specials.RemoveAt(21);
        Assert.Throws<InvalidDataException>(() => PokedexMapService.Save(t.Project, map));
        reread.Species[399].Lists[0].Areas.Remove(22);
        PokedexAreaService.Save(t.Project, reread);
        PokedexMapService.Save(t.Project, map);
        Assert.Equal((21, 50), (PokedexAreaService.Counts(t.Project).Specials, PokedexAreaService.Counts(t.Project).Routes));
        Assert.Equal(listsBefore.Species[399].Lists[5].Areas, PokedexAreaService.Load(t.Project).Species[399].Lists[5].Areas);
    }

    [Fact]
    public void ListsNamingNewPlacesNeedTheMapSavedFirst()
    {
        TestProject t = fixture.CreateProject();
        PokedexAreas lists = PokedexAreaService.Load(t.Project);
        lists.Species[10].Lists[6].Areas.Add(51);
        Assert.Throws<InvalidDataException>(() => PokedexAreaService.Save(t.Project, lists));
        PokedexAreaMap map = PokedexMapService.Load(t.Project);
        map.Routes.Add(new RoutePlace { Header = 342 });
        PokedexMapService.Save(t.Project, map);
        PokedexAreaService.Save(t.Project, lists);
        Assert.Contains(51, PokedexAreaService.Load(t.Project).Species[10].Lists[6].Areas);
        string names = string.Join(",", PokedexAreaService.PlaceNames(t.Project, t.Metadata, false));
        Assert.Equal(51, PokedexAreaService.PlaceNames(t.Project, t.Metadata, false).Count);
        Assert.Contains("Route 201", names);
    }

    [Fact]
    public void AddedPlacesAndTheListsNamingThemReplayFromTheJournal()
    {
        TestProject a = fixture.CreateProject(), b = fixture.CreateProject();
        string journal = Path.Combine(a.Project.RootPath, EditJournal.FileName);
        EditJournal.Open(a.Project, a.Metadata, journalPath: journal);
        try
        {
            PokedexAreaMap map = PokedexMapService.Load(a.Project);
            var route = new RoutePlace { Header = 343 };
            route.SetCell(2, 4, true);
            route.SetCell(3, 4, true);
            map.Routes.Add(route);
            map.Specials.Add(new SpecialPlace { Header = 207, X = 9, Y = 3 });
            PokedexMapService.Save(a.Project, map);
            PokedexAreas lists = PokedexAreaService.Load(a.Project);
            lists.Species[399].Lists[5].Areas.Add(51);
            lists.Species[399].Lists[2].Areas.Add(22);
            PokedexAreaService.Save(a.Project, lists);
        }
        finally
        {
            EditJournal.Close();
        }

        Assert.True(EditJournalReplayer.Replay(b.Project, b.Metadata, journal) > 0);
        PokedexAreaMap replayed = PokedexMapService.Load(b.Project);
        Assert.Equal((22, 51), (replayed.Specials.Count, replayed.Routes.Count));
        Assert.Equal(["##"], replayed.Routes[50].Rows);
        Assert.Equal((9, 3, 207u), (replayed.Specials[21].X, replayed.Specials[21].Y, replayed.Specials[21].Header));
        PokedexAreas replayedLists = PokedexAreaService.Load(b.Project);
        Assert.Contains(51, replayedLists.Species[399].Lists[5].Areas);
        Assert.Contains(22, replayedLists.Species[399].Lists[2].Areas);
    }
}
