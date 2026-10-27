using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class MapDeletionTests(PlatinumFixture fixture)
{
    [Fact]
    public void DeletingAMiddleMapEmptiesItsSquaresAndKeepsEveryNumber()
    {
        TestProject t = fixture.CreateProject();
        int count = HeaderFileService.NextFileId(t.Project.MapsDirectory);
        int matrixId = MatrixService.Add(t.Project);
        MatrixDocument matrix = MatrixService.Load(t.Project, matrixId);
        matrix.Resize(1, 2);
        matrix.Maps[0, 0] = 10;
        matrix.Maps[0, 1] = 11;
        MatrixService.Save(t.Project, matrix);
        byte[] eleven = File.ReadAllBytes(MapFileService.GetPath(t.Project, 11));

        MapDeletionService.Delete(t.Project, t.Metadata, 10);

        Assert.Equal(count, HeaderFileService.NextFileId(t.Project.MapsDirectory));
        Assert.Equal(eleven, File.ReadAllBytes(MapFileService.GetPath(t.Project, 11)));
        MapFileParts placeholder = MapFileService.Load(t.Project, t.Metadata, 10);
        Assert.Empty(placeholder.Buildings);
        Assert.All(placeholder.Permissions, b => Assert.Equal(0, b));
        MatrixDocument after = MatrixService.Load(t.Project, matrixId);
        Assert.Equal(MatrixDocument.EmptyMap, after.Maps[0, 0]);
        Assert.Equal(11, after.Maps[0, 1]);
    }

    [Fact]
    public void TheLastMapFileIsRemoved()
    {
        TestProject t = fixture.CreateProject();
        int last = HeaderFileService.NextFileId(t.Project.MapsDirectory) - 1;
        MapDeletionService.Delete(t.Project, t.Metadata, last);
        Assert.Equal(last, HeaderFileService.NextFileId(t.Project.MapsDirectory));
    }

    [Fact]
    public void RemovingUnusedMapDataClosesTheGapAndKeepsMatricesPointingAtTheSameMaps()
    {
        TestProject t = fixture.CreateProject();
        int count = HeaderFileService.NextFileId(t.Project.MapsDirectory);
        IMapHeaderStore store = MapHeaderStoreFactory.Create(t.Project, t.Metadata)!;
        int matrixId = MatrixService.Add(t.Project);
        MatrixDocument matrix = MatrixService.Load(t.Project, matrixId);
        matrix.Resize(1, 2);
        matrix.Maps[0, 0] = 10;
        matrix.Maps[0, 1] = 11;
        MatrixService.Save(t.Project, matrix);
        store.Save(store.Load(5) with { MatrixId = (ushort)matrixId });
        byte[] eleven = File.ReadAllBytes(MapFileService.GetPath(t.Project, 11));
        MapDeletionService.Delete(t.Project, t.Metadata, 10);

        MapDataCleanupService.Apply(t.Project, t.Metadata, merge: false, remove: true);

        int newMatrix = store.Load(5).MatrixId;
        MatrixDocument after = MatrixService.Load(t.Project, newMatrix);
        Assert.Equal(MatrixDocument.EmptyMap, after.Maps[0, 0]);
        Assert.Equal(eleven, File.ReadAllBytes(MapFileService.GetPath(t.Project, after.Maps[0, 1])));
        Assert.True(HeaderFileService.NextFileId(t.Project.MapsDirectory) < count);
    }
}
