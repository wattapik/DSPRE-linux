using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class HiddenItemTests(PlatinumFixture fixture)
{
    [Fact]
    public void PlatinumsTableIsFoundWhereTheCodeKeepsItsAddressAndCount()
    {
        TestProject t = fixture.CreateProject();

        HiddenItemLayout? layout = HiddenItemService.Locate(t.Project, t.Metadata, out string? problem);

        Assert.Null(problem);
        Assert.Equal(new HiddenItemLayout(HiddenItemLayoutKind.LiteralPool, 0xEA378, 257, -1, -1), layout);
        HiddenItemTable table = HiddenItemService.Load(t.Project, t.Metadata);
        Assert.Equal(257, table.Entries.Count);
        Assert.Equal((ushort)41, table.Entries[0].ItemId);
        Assert.Equal((ushort)1, table.Entries[1].ScriptId);
        Assert.Equal((ushort)256, table.Entries[229].ScriptId);
        Assert.Contains(table.Entries, entry => entry.Extra == 0);
    }

    [Fact]
    public void AnUnchangedSaveLeavesThePlatinumArm9Identical()
    {
        TestProject t = fixture.CreateProject();
        byte[] before = File.ReadAllBytes(t.Project.Arm9Path);

        HiddenItemService.Save(t.Project, t.Metadata, HiddenItemService.Load(t.Project, t.Metadata).Entries);

        Assert.Equal(before, File.ReadAllBytes(t.Project.Arm9Path));
    }

    [Fact]
    public void EditsPersistAndFreedSlotsGetAnIdNoScriptAsksFor()
    {
        TestProject t = fixture.CreateProject();
        byte[] before = File.ReadAllBytes(t.Project.Arm9Path);
        List<HiddenItemEntry> entries = HiddenItemService.Load(t.Project, t.Metadata).Entries;
        entries[3].ItemId = 17;
        entries[3].Amount = 5;
        entries[3].Extra = 1;
        entries.RemoveRange(250, 7);

        HiddenItemService.Save(t.Project, t.Metadata, entries);

        HiddenItemTable after = HiddenItemService.Load(t.Project, t.Metadata);
        Assert.Equal(250, after.Entries.Count);
        Assert.Equal(257, after.Capacity);
        Assert.Equal((ushort)17, after.Entries[3].ItemId);
        Assert.Equal((byte)5, after.Entries[3].Amount);
        Assert.Equal((byte)1, after.Entries[3].Extra);
        byte[] arm9 = File.ReadAllBytes(t.Project.Arm9Path);
        int freed = 0xEA378 + 250 * 8;
        Assert.Equal(0xFFFF, BitConverter.ToUInt16(arm9, freed + 6));
        Assert.Equal(0, BitConverter.ToUInt16(arm9, freed));
        for (int i = 0; i < arm9.Length; i++)
        {
            if (i >= 0xEA378 && i < 0xEA378 + 257 * 8) continue;
            Assert.True(arm9[i] == before[i], $"byte 0x{i:X} outside the table changed");
        }
    }

    [Fact]
    public void ATableEditedBeforeIsStillFound()
    {
        TestProject t = fixture.CreateProject();
        List<HiddenItemEntry> entries = HiddenItemService.Load(t.Project, t.Metadata).Entries;
        foreach (HiddenItemEntry entry in entries.Take(40))
        {
            entry.ItemId = 300;
            entry.Amount = 99;
        }

        HiddenItemService.Save(t.Project, t.Metadata, entries);

        Assert.Equal(0xEA378, HiddenItemService.Locate(t.Project, t.Metadata, out _)!.TableOffset);
    }

    [Fact]
    public void SavingRefusesTooManyEntriesAndEntriesWithoutAnItem()
    {
        TestProject t = fixture.CreateProject();
        List<HiddenItemEntry> entries = HiddenItemService.Load(t.Project, t.Metadata).Entries;

        entries.Add(new HiddenItemEntry { ItemId = 1, ScriptId = 300 });
        Assert.Throws<InvalidDataException>(() => HiddenItemService.Save(t.Project, t.Metadata, entries));
        entries.RemoveAt(entries.Count - 1);
        entries[0].ItemId = 0;
        Assert.Throws<InvalidDataException>(() => HiddenItemService.Save(t.Project, t.Metadata, entries));
    }

    [Fact]
    public void FirstFreeScriptIdSkipsUsedOnes()
    {
        Assert.Equal((ushort)2, HiddenItemService.FirstFreeScriptId([new() { ScriptId = 0 }, new() { ScriptId = 1 }, new() { ScriptId = 3 }]));
    }

    [Fact]
    public void EveryHiddenItemSpawnableOfVanillaPlatinumHasAnEntry()
    {
        TestProject t = fixture.CreateProject();
        HashSet<ushort> ids = HiddenItemService.Load(t.Project, t.Metadata).Entries.Select(entry => entry.ScriptId).ToHashSet();

        List<HiddenItemUse> uses = HiddenItemUsageService.Find(t.Project, t.Metadata);

        Assert.True(uses.Count > 200);
        Assert.All(uses, use => Assert.Contains((ushort)use.ScriptId, ids));
        Assert.Contains(uses, use => use.HeaderId is not null);
    }

    [Fact]
    public void DiamondPearlIsRefused()
    {
        RomGameMetadata.TryResolve("ADAE", out RomGameMetadata? diamond);

        Assert.Null(HiddenItemService.Locate([], diamond!, out string? problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void UsHeartGoldsTableIsFoundBySearchAtLegacysOffsets()
    {
        string? arm9 = FindHeartGoldArm9();
        if (arm9 is null) return;
        RomGameMetadata.TryResolve("IPKE", out RomGameMetadata? hg);

        HiddenItemLayout? layout = HiddenItemService.Locate(File.ReadAllBytes(arm9), hg!, out string? problem);

        Assert.Null(problem);
        Assert.Equal(new HiddenItemLayout(HiddenItemLayoutKind.ThumbImmediate, 0xFA558, 231, 0x405E4, 0x405E8), layout);
    }

    private static string? FindHeartGoldArm9()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "hgssverity", "verity_DSPRE_contents", "arm9", "arm9.bin");
            if (File.Exists(path)) return path;
        }

        return null;
    }
}
