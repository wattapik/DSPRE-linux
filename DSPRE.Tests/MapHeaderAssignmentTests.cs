using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class MapHeaderAssignmentTests(PlatinumFixture fixture)
{
    [Fact]
    public void AMapNoHeaderReachesBecomesReachableThroughTheChosenHeader()
    {
        TestProject t = fixture.CreateProject();
        IMapHeaderStore store = MapHeaderStoreFactory.Create(t.Project, t.Metadata)!;
        int matrixId = MatrixService.Add(t.Project);
        MatrixDocument matrix = MatrixService.Load(t.Project, matrixId);
        matrix.Maps[0, 0] = 9999;
        MatrixService.Save(t.Project, matrix);
        Assert.Null(AreaDataService.FindHeaderForMap(t.Project, store.Load, store.Count, 9999));
        MapHeaderRecord before = store.Load(5);

        MapHeaderAssignmentService.Result result = MapHeaderAssignmentService.Assign(t.Project, store, 9999, 5);

        Assert.Equal(matrixId, result.MatrixId);
        Assert.True(result.AddedHeadersSection);
        Assert.Equal((ushort?)5, AreaDataService.FindHeaderForMap(t.Project, store.Load, store.Count, 9999));
        Assert.Equal(matrixId, store.Load(5).MatrixId);
        Assert.Equal(before with { MatrixId = (ushort)matrixId }, store.Load(5));
        Assert.Equal((ushort)5, MatrixService.Load(t.Project, matrixId).Headers[0, 0]);
    }

    [Fact]
    public void ChoosingAnotherHeaderForAMapInAMatrixWithHeadersChangesOnlyItsSquare()
    {
        TestProject t = fixture.CreateProject();
        IMapHeaderStore store = MapHeaderStoreFactory.Create(t.Project, t.Metadata)!;
        int matrixId = MatrixService.Add(t.Project);
        MatrixDocument matrix = MatrixService.Load(t.Project, matrixId);
        matrix.Resize(1, 2);
        matrix.HasHeadersSection = true;
        (matrix.Maps[0, 0], matrix.Maps[0, 1]) = (9998, 9999);
        (matrix.Headers[0, 0], matrix.Headers[0, 1]) = (7, 7);
        MatrixService.Save(t.Project, matrix);
        store.Save(store.Load(7) with { MatrixId = (ushort)matrixId });

        MapHeaderAssignmentService.Assign(t.Project, store, 9999, 8);

        MatrixDocument after = MatrixService.Load(t.Project, matrixId);
        Assert.Equal((ushort)7, after.Headers[0, 0]);
        Assert.Equal((ushort)8, after.Headers[0, 1]);
        Assert.Equal(matrixId, store.Load(8).MatrixId);
    }
}
