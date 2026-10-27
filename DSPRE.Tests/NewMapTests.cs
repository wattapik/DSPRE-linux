using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class NewMapTests(PlatinumFixture fixture)
{
    [Fact]
    public async Task WithoutTheDynamicHeaderPatchAMapCannotBeAddedAndNothingIsChanged()
    {
        TestProject t = fixture.CreateProject();
        if (NewMapService.CanAddMaps(t.Project, t.Metadata)) return;
        int areaData = AreaDataService.GetIds(t.Project).Count;
        int matrices = MatrixService.GetIds(t.Project).Count;

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => NewMapService.WireAsync(t.Project, t.Metadata, "unused", 0, "x", -1));

        Assert.Equal(NewMapService.RequirementMessage, error.Message);
        Assert.Equal(areaData, AreaDataService.GetIds(t.Project).Count);
        Assert.Equal(matrices, MatrixService.GetIds(t.Project).Count);
    }

    [Fact]
    public void ANewEncounterFileHasTheSizeOfTheOthersAndNeverEncountersAnything()
    {
        TestProject t = fixture.CreateProject();
        IReadOnlyList<int> before = WildEncounterService.GetFileIds(t.Project);

        int id = WildEncounterService.AddBlank(t.Project);

        Assert.Equal(before.Max() + 1, id);
        byte[] blank = File.ReadAllBytes(Path.Combine(t.Project.EncountersDirectory, $"{id:D4}"));
        Assert.Equal(new FileInfo(Path.Combine(t.Project.EncountersDirectory, $"{before[0]:D4}")).Length, blank.Length);
        Assert.All(blank, b => Assert.Equal(0, b));
        Assert.Equal(before.Count + 1, WildEncounterService.GetFileIds(t.Project).Count);
    }
}
