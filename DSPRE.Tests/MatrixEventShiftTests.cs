using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class MatrixEventShiftTests(PlatinumFixture fixture)
{
    [Fact]
    public void ExpandingAMatrixAroundAMapMovesItsEventsWithIt()
    {
        TestProject t = fixture.CreateProject();
        IMapHeaderStore store = MapHeaderStoreFactory.Create(t.Project, t.Metadata)!;
        int matrixId = MatrixService.Add(t.Project);
        MatrixDocument matrix = MatrixService.Load(t.Project, matrixId);
        matrix.Maps[0, 0] = 9999;
        MatrixService.Save(t.Project, matrix);
        int eventFileId = HeaderFileService.AddEventFile(t.Project);
        store.Save(store.Load(5) with { MatrixId = (ushort)matrixId, EventFileId = (ushort)eventFileId });
        MapEventFile events = MapEventService.Load(t.Project, eventFileId);
        events.Warps.Add(new WarpEvent { MapPositionX = 11, MapPositionY = 11, MatrixPositionX = 0, MatrixPositionY = 0 });
        events.Overworlds.Add(new OverworldEvent { MapPositionX = 16, MapPositionY = 12, MatrixPositionX = 0, MatrixPositionY = 0 });
        MapEventService.Save(t.Project, events);

        MatrixDocument bigger = MatrixService.Load(t.Project, matrixId);
        bigger.Resize(3, 3);
        bigger.Maps[0, 0] = 173;
        bigger.Maps[1, 1] = 9999;
        MatrixEventShiftService.Result result = MatrixEventShiftService.Save(t.Project, t.Metadata, bigger);

        Assert.Equal(2, result.EventsMoved);
        MapEventFile after = MapEventService.Load(t.Project, eventFileId);
        Assert.Equal((1, 1, 11, 11), (after.Warps[0].MatrixPositionX, after.Warps[0].MatrixPositionY, after.Warps[0].MapPositionX, after.Warps[0].MapPositionY));
        Assert.Equal((1, 1, 16, 12), (after.Overworlds[0].MatrixPositionX, after.Overworlds[0].MatrixPositionY, after.Overworlds[0].MapPositionX, after.Overworlds[0].MapPositionY));
    }

    [Fact]
    public void AnUnchangedOrAddedMapMovesNothing()
    {
        TestProject t = fixture.CreateProject();
        int matrixId = MatrixService.Add(t.Project);
        MatrixDocument matrix = MatrixService.Load(t.Project, matrixId);
        matrix.Maps[0, 0] = 9999;
        MatrixService.Save(t.Project, matrix);

        MatrixDocument bigger = MatrixService.Load(t.Project, matrixId);
        bigger.Resize(2, 2);
        bigger.Maps[1, 1] = 173;

        Assert.Equal(0, MatrixEventShiftService.Save(t.Project, t.Metadata, bigger).Moves.Count);
    }
}
