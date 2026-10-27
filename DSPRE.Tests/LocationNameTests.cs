using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class LocationNameTests(PlatinumFixture fixture)
{
    [Fact]
    public void ANewLocationNameIsTheNextNumberAndShowsInTheList()
    {
        TestProject t = fixture.CreateProject();
        IReadOnlyList<HeaderOption> before = t.Project.GetLocationNames(t.Metadata);

        int id = LocationNameService.Add(t.Project, t.Metadata, "  Test Haven ");

        Assert.Equal(before.Count, id);
        IReadOnlyList<HeaderOption> after = t.Project.GetLocationNames(t.Metadata);
        Assert.Equal(before.Count + 1, after.Count);
        Assert.Equal("Test Haven", after[id].Label);
        Assert.Equal(before.Select(option => option.Label), after.Take(before.Count).Select(option => option.Label));
        Assert.Throws<ArgumentException>(() => LocationNameService.Add(t.Project, t.Metadata, "  "));
    }
}
