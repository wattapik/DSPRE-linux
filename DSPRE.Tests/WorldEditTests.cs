using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class WorldEditTests(PlatinumFixture fixture)
{
    [Fact]
    public void MapHeader_EditPersists_OthersUntouched()
    {
        TestProject t = fixture.CreateProject();
        IMapHeaderStore store = MapHeaderStoreFactory.Create(t.Project, t.Metadata)!;
        MapHeaderRecord before = store.Load(3);
        MapHeaderRecord other = store.Load(4);

        store.Save(before with { MusicDayId = 1234, WeatherId = 7, LocationNameId = 9, CameraAngleId = 2, Flags = (byte)(before.Flags ^ 1) });

        MapHeaderRecord after = store.Load(3);
        Assert.Equal(1234, after.MusicDayId);
        Assert.Equal(7, after.WeatherId);
        Assert.Equal(9, after.LocationNameId);
        Assert.Equal(2, after.CameraAngleId);
        Assert.Equal(before.MatrixId, after.MatrixId);
        Assert.Equal(before.EventFileId, after.EventFileId);
        Assert.Equal(other, store.Load(4));
    }

    [Fact]
    public void MapHeader_SerializeRoundTrips()
    {
        TestProject t = fixture.CreateProject();
        IMapHeaderStore store = MapHeaderStoreFactory.Create(t.Project, t.Metadata)!;
        for (ushort id = 0; id < 40; id++)
        {
            MapHeaderRecord header = store.Load(id);
            Assert.Equal(header, MapHeaderRecord.Parse(header.Serialize(), id, t.Metadata.HeaderFormat));
        }
    }

    [Fact]
    public void MapHeader_OutOfRangeIdIsRejected()
    {
        TestProject t = fixture.CreateProject();
        IMapHeaderStore store = MapHeaderStoreFactory.Create(t.Project, t.Metadata)!;
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Load((ushort)store.Count));
    }

    [Fact]
    public void HeaderInternalName_EditPersists()
    {
        RomProject p = fixture.CreateProject().Project;
        p.SaveInternalName(3, "T99R0101");
        Assert.Equal("T99R0101", p.GetInternalName(3));
    }

    private int FindEventFileWithOverworld(RomProject p) =>
        Directory.GetFiles(p.EventsDirectory).Select(f => int.Parse(Path.GetFileName(f)))
            .First(id => MapEventService.Load(p, id).Overworlds.Count > 0 && MapEventService.Load(p, id).Warps.Count > 0);

    [Fact]
    public void Events_EditAddRemovePersist()
    {
        RomProject p = fixture.CreateProject().Project;
        int id = FindEventFileWithOverworld(p);
        MapEventFile file = MapEventService.Load(p, id);
        int overworlds = file.Overworlds.Count, warps = file.Warps.Count;

        file.Overworlds[0].ScriptNumber = 4242;
        file.Overworlds[0].MapPositionX = 5;
        file.Overworlds.Add(file.Overworlds[0].Clone());
        file.Warps[0].DestinationHeaderId = 77;
        file.Warps.RemoveAt(file.Warps.Count - 1);
        file.Spawnables.Add(new SpawnableEvent { ScriptNumber = 9, MapPositionX = 2, MapPositionY = 3 });
        MapEventService.Save(p, file);

        MapEventFile after = MapEventService.Load(p, id);
        Assert.Equal(overworlds + 1, after.Overworlds.Count);
        Assert.Equal(warps - 1, after.Warps.Count);
        Assert.Equal(4242, after.Overworlds[0].ScriptNumber);
        Assert.Equal(5, after.Overworlds[0].MapPositionX);
        Assert.Equal(77, after.Warps[0].DestinationHeaderId);
        Assert.Contains(after.Spawnables, s => s.ScriptNumber == 9 && s.MapPositionX == 2 && s.MapPositionY == 3);
    }

    [Fact]
    public void Events_UnchangedSaveIsByteIdentical()
    {
        RomProject p = fixture.CreateProject().Project;
        foreach (string path in Directory.GetFiles(p.EventsDirectory).Take(60))
        {
            int id = int.Parse(Path.GetFileName(path));
            byte[] original = File.ReadAllBytes(path);
            MapEventService.Save(p, MapEventService.Load(p, id));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void WildEncounters_EditPersists()
    {
        RomProject p = fixture.CreateProject().Project;
        int id = Directory.GetFiles(p.EncountersDirectory).Select(f => int.Parse(Path.GetFileName(f)))
            .First(i => WildEncounterService.Load(p, i).Rows.Any(r => r.HasMaxLevel));
        WildEncounterFile file = WildEncounterService.Load(p, id);
        WildEncounterRow row = file.Rows.First(r => r.HasMaxLevel);
        int index = file.Rows.IndexOf(row);
        row.Species = 150;
        row.MaxLevel = 63;
        WildEncounterService.Save(p, file);

        WildEncounterRow after = WildEncounterService.Load(p, id).Rows[index];
        Assert.Equal(150, after.Species);
        Assert.Equal(63, after.MaxLevel);
    }

    [Fact]
    public void WildEncounters_UnchangedSaveIsByteIdentical()
    {
        RomProject p = fixture.CreateProject().Project;
        foreach (string path in Directory.GetFiles(p.EncountersDirectory).Take(40))
        {
            int id = int.Parse(Path.GetFileName(path));
            byte[] original = File.ReadAllBytes(path);
            WildEncounterService.Save(p, WildEncounterService.Load(p, id));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void Matrix_CellEditPersists()
    {
        RomProject p = fixture.CreateProject().Project;
        int id = MatrixService.GetIds(p).First();
        MatrixDocument matrix = MatrixService.Load(p, id);
        ushort old = matrix.Maps[0, 0];
        matrix.Maps[0, 0] = (ushort)(old == 5 ? 6 : 5);
        ushort written = matrix.Maps[0, 0];
        MatrixService.Save(p, matrix);

        Assert.Equal(written, MatrixService.Load(p, id).Maps[0, 0]);
    }

    [Fact]
    public void Matrix_UnchangedSaveIsByteIdentical()
    {
        RomProject p = fixture.CreateProject().Project;
        foreach (int id in MatrixService.GetIds(p).Take(20))
        {
            string path = Path.Combine(p.MatricesDirectory, id.ToString("D4"));
            byte[] original = File.ReadAllBytes(path);
            MatrixService.Save(p, MatrixService.Load(p, id));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void AreaData_EditPersists()
    {
        RomProject p = fixture.CreateProject().Project;
        int id = AreaDataService.GetIds(p).First();
        AreaDataRecord record = AreaDataService.Load(p, id, isHeartGoldSoulSilver: false);
        record.MapTileset = 3;
        record.BuildingsTileset = 4;
        record.LightType = 2;
        AreaDataService.Save(p, record, isHeartGoldSoulSilver: false);

        AreaDataRecord after = AreaDataService.Load(p, id, isHeartGoldSoulSilver: false);
        Assert.Equal(3, after.MapTileset);
        Assert.Equal(4, after.BuildingsTileset);
        Assert.Equal(2, after.LightType);
    }

    [Fact]
    public void MapPermissions_EditPersists()
    {
        RomProject p = fixture.CreateProject().Project;
        int mapId = Directory.GetFiles(p.MapsDirectory).Select(f => int.Parse(Path.GetFileName(f))).First();
        (byte[,] types, byte[,] collisions) = MapPermissionsService.Load(p, mapId);
        types[1, 2] = 0x0C;
        collisions[3, 4] = 0x80;
        MapPermissionsService.Save(p, mapId, types, collisions);

        (byte[,] t2, byte[,] c2) = MapPermissionsService.Load(p, mapId);
        Assert.Equal(0x0C, t2[1, 2]);
        Assert.Equal(0x80, c2[3, 4]);
    }

    [Fact]
    public void FlyWarps_EditPersists()
    {
        TestProject t = fixture.CreateProject();
        List<FlyWarpEntry> entries = FlyWarpService.Load(t.Project, t.Metadata);
        entries[0].HeaderIdFly = 321;
        entries[0].GlobalX = 100;
        FlyWarpService.Save(t.Project, t.Metadata, entries);

        FlyWarpEntry after = FlyWarpService.Load(t.Project, t.Metadata)[0];
        Assert.Equal(321, after.HeaderIdFly);
        Assert.Equal(100, after.GlobalX);
    }
}
