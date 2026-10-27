using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class BattleDataEditTests(PlatinumFixture fixture)
{
    private static string TrainerMessagePath(RomProject p, string name) => Path.Combine(p.FilesPath, "poketool", "trmsg", name);

    [Fact]
    public void Growth_StandardFormulasMatchTheGameTables()
    {
        TestProject t = fixture.CreateProject();
        Assert.True(GrowthTableService.IsSupported(t.Project, t.Metadata));
        GrowthTables tables = GrowthTableService.Load(t.Project);
        Assert.Equal(8, tables.Curves.Count);
        for (int curve = 0; curve < 8; curve++)
        {
            Assert.Equal(GrowthTableService.Levels, tables.Curves[curve].Experience.Count);
            for (int level = 0; level < GrowthTableService.Levels; level++)
            {
                uint expected = GrowthTableService.Standard(curve, level);
                Assert.True(expected == tables.Curves[curve].Experience[level], $"curve {curve} level {level}: formula {expected}, table {tables.Curves[curve].Experience[level]}");
            }
        }
    }

    [Fact]
    public void Growth_UnchangedSaveKeepsMembersAndEditPersists()
    {
        TestProject t = fixture.CreateProject();
        string path = GrowthTableService.ArchivePath(t.Project);
        byte[][] before = Enumerable.Range(0, NarcFile.MemberCount(path)).Select(i => NarcFile.ReadMember(path, i)).ToArray();
        GrowthTables tables = GrowthTableService.Load(t.Project);
        GrowthTableService.Save(t.Project, tables);
        for (int i = 0; i < before.Length; i++) Assert.Equal(before[i], NarcFile.ReadMember(path, i));

        tables.Curves[4].Experience[100] = 123456;
        GrowthTableService.Save(t.Project, tables);
        Assert.Equal(123456u, GrowthTableService.Load(t.Project).Curves[4].Experience[100]);
        Assert.Equal(1000000u, GrowthTableService.Load(t.Project).Curves[0].Experience[100]);
    }

    [Fact]
    public void Frontier_UnchangedSavesKeepEveryMember()
    {
        TestProject t = fixture.CreateProject();
        Assert.True(FrontierService.IsSupported(t.Project, t.Metadata));
        string tower = Path.Combine(t.Project.FilesPath, "battle", "b_pl_tower", "pl_btdpm.narc");
        string stage = Path.Combine(t.Project.FilesPath, "battle", "b_pl_stage", "pl_bsdpm.narc");
        string trainers = Path.Combine(t.Project.FilesPath, "battle", "b_pl_tower", "pl_btdtr.narc");
        byte[] towerBefore = File.ReadAllBytes(tower), stageBefore = File.ReadAllBytes(stage), trainersBefore = File.ReadAllBytes(trainers);

        FrontierService.SaveSets(t.Project, FrontierFacility.Tower, FrontierService.LoadSets(t.Project, FrontierFacility.Tower));
        FrontierService.SaveSets(t.Project, FrontierFacility.Stage, FrontierService.LoadSets(t.Project, FrontierFacility.Stage));
        FrontierService.SaveTrainers(t.Project, FrontierService.LoadTrainers(t.Project));
        Assert.Equal(towerBefore, File.ReadAllBytes(tower));
        Assert.Equal(stageBefore, File.ReadAllBytes(stage));
        Assert.Equal(trainersBefore, File.ReadAllBytes(trainers));

        // Rewriting every record from its parsed form reproduces the original bytes, padding included.
        FrontierTrainers parsed = FrontierService.LoadTrainers(t.Project);
        parsed.Trainers[0].SetIds.Add(7);
        parsed.Trainers[0].SetIds.RemoveAt(parsed.Trainers[0].SetIds.Count - 1);
        FrontierService.SaveTrainers(t.Project, parsed);
        Assert.Equal(trainersBefore, File.ReadAllBytes(trainers));
    }

    [Fact]
    public void Frontier_EditsPersist()
    {
        TestProject t = fixture.CreateProject();
        FrontierPokemonSets sets = FrontierService.LoadSets(t.Project, FrontierFacility.Tower);
        Assert.Equal(951, sets.Sets.Count);
        FrontierPokemon first = sets.Sets[1];
        first.Species = 25;
        first.Moves[2] = 85;
        first.EvFlags = 0b101010;
        first.Nature = 10;
        first.Item = 234;
        FrontierService.SaveSets(t.Project, FrontierFacility.Tower, sets);
        FrontierPokemon after = FrontierService.LoadSets(t.Project, FrontierFacility.Tower).Sets[1];
        Assert.Equal((25, 85, 0b101010, 10, 234), (after.Species, after.Moves[2], after.EvFlags, after.Nature, after.Item));
        Assert.Equal(sets.Sets[2].Species, FrontierService.LoadSets(t.Project, FrontierFacility.Tower).Sets[2].Species);

        FrontierTrainers trainers = FrontierService.LoadTrainers(t.Project);
        Assert.Equal(315, trainers.Trainers.Count);
        trainers.Trainers[3].SetIds = [5, 6, 7];
        trainers.Trainers[3].TrainerClass = 9;
        FrontierService.SaveTrainers(t.Project, trainers);
        FrontierTrainer reread = FrontierService.LoadTrainers(t.Project).Trainers[3];
        Assert.Equal([5, 6, 7], reread.SetIds.Select(id => (int)id));
        Assert.Equal(9, reread.TrainerClass);
        Assert.Equal(trainers.Trainers[4].SetIds, FrontierService.LoadTrainers(t.Project).Trainers[4].SetIds);
    }

    [Fact]
    public void TrainerMessages_UnchangedSaveKeepsTableOffsetsAndText()
    {
        TestProject t = fixture.CreateProject();
        Assert.True(TrainerMessageService.IsSupported(t.Project, t.Metadata));
        string table = TrainerMessagePath(t.Project, "trtbl.narc"), offsets = TrainerMessagePath(t.Project, "trtblofs.narc");
        byte[] tableBefore = NarcFile.ReadMember(table, 0), offsetsBefore = NarcFile.ReadMember(offsets, 0);
        List<string> textBefore = TextArchiveService.Load(t.Project, TrainerMessageService.TextArchiveId(t.Metadata), t.Metadata).Messages;

        TrainerMessageTable loaded = TrainerMessageService.Load(t.Project, t.Metadata);
        Assert.Equal(tableBefore.Length / 4, loaded.Entries.Count);
        TrainerMessageService.Save(t.Project, t.Metadata, loaded);
        Assert.Equal(tableBefore, NarcFile.ReadMember(table, 0));
        Assert.Equal(offsetsBefore, NarcFile.ReadMember(offsets, 0));
        Assert.Equal(textBefore.Take(loaded.Entries.Count), TextArchiveService.Load(t.Project, TrainerMessageService.TextArchiveId(t.Metadata), t.Metadata).Messages);
    }

    [Fact]
    public void TrainerMessages_AddEditAndRemovePersistWithOffsets()
    {
        TestProject t = fixture.CreateProject();
        TrainerMessageTable loaded = TrainerMessageService.Load(t.Project, t.Metadata);
        int trainerWithNone = Enumerable.Range(1, 900).First(id => loaded.Entries.All(entry => entry.Trainer != id));
        loaded.Entries.Add(new TrainerMessageEntry { Trainer = trainerWithNone, Trigger = 0, Text = "Brand new line" });
        loaded.Entries.Add(new TrainerMessageEntry { Trainer = trainerWithNone, Trigger = 1, Text = "Second line" });
        TrainerMessageEntry edited = loaded.Entries[5];
        edited.Text = "Edited text";
        loaded.Entries.RemoveAt(8);
        TrainerMessageService.Save(t.Project, t.Metadata, loaded);

        TrainerMessageTable after = TrainerMessageService.Load(t.Project, t.Metadata);
        Assert.Equal(loaded.Entries.Count, after.Entries.Count);
        Assert.Contains(after.Entries, entry => entry.Trainer == trainerWithNone && entry.Trigger == 0 && entry.Text == "Brand new line");
        Assert.Contains(after.Entries, entry => entry.Trainer == trainerWithNone && entry.Trigger == 1 && entry.Text == "Second line");
        Assert.Contains(after.Entries, entry => entry.Trainer == edited.Trainer && entry.Trigger == edited.Trigger && entry.Text == "Edited text");

        byte[] table = NarcFile.ReadMember(TrainerMessagePath(t.Project, "trtbl.narc"), 0), offsets = NarcFile.ReadMember(TrainerMessagePath(t.Project, "trtblofs.narc"), 0);
        for (int trainer = 0; trainer < offsets.Length / 2; trainer++)
        {
            int offset = BitConverter.ToUInt16(offsets, trainer * 2);
            int count = after.Entries.Count(entry => entry.Trainer == trainer);
            if (count == 0)
            {
                Assert.Equal(0, offset);
                continue;
            }

            for (int i = 0; i < count; i++) Assert.Equal(trainer, BitConverter.ToUInt16(table, offset + i * 4));
        }
    }

    [Fact]
    public void Journal_RecordsAndReplaysTheBattleDataEdits()
    {
        TestProject a = fixture.CreateProject(), b = fixture.CreateProject();
        string journal = Path.Combine(a.Project.RootPath, EditJournal.FileName);
        EditJournal.Open(a.Project, a.Metadata, journalPath: journal);
        try
        {
            TrainerMessageTable messages = TrainerMessageService.Load(a.Project, a.Metadata);
            messages.Entries[2].Text = "Journaled battle line";
            messages.Entries.Add(new TrainerMessageEntry { Trainer = 900, Trigger = 2, Text = "A new trainer speaks" });
            TrainerMessageService.Save(a.Project, a.Metadata, messages);

            GrowthTables growth = GrowthTableService.Load(a.Project);
            growth.Curves[5].Experience[50] = 4242;
            GrowthTableService.Save(a.Project, growth);

            FrontierPokemonSets sets = FrontierService.LoadSets(a.Project, FrontierFacility.Tower);
            sets.Sets[10].Species = 150;
            sets.Sets[10].Moves[3] = 94;
            FrontierService.SaveSets(a.Project, FrontierFacility.Tower, sets);
            FrontierPokemonSets stage = FrontierService.LoadSets(a.Project, FrontierFacility.Stage);
            stage.Sets[7].Nature = 12;
            FrontierService.SaveSets(a.Project, FrontierFacility.Stage, stage);

            FrontierTrainers trainers = FrontierService.LoadTrainers(a.Project);
            trainers.Trainers[5].SetIds = [3, 1, 4, 1, 5];
            FrontierService.SaveTrainers(a.Project, trainers);
        }
        finally
        {
            EditJournal.Close();
        }

        string[] lines = File.ReadAllLines(journal);
        Assert.Contains(lines, line => line.Contains("TrainerMessages") || line.Contains("Entries"));
        Assert.Contains(lines, line => line.Contains("Curves"));
        Assert.Contains(lines, line => line.Contains("Sets[10]") || line.Contains("[10]"));

        Assert.True(EditJournalReplayer.Replay(b.Project, b.Metadata, journal) > 0);

        TrainerMessageTable replayed = TrainerMessageService.Load(b.Project, b.Metadata);
        TrainerMessageTable original = TrainerMessageService.Load(a.Project, a.Metadata);
        Assert.Equal(original.Entries.Select(e => (e.Trainer, e.Trigger, e.Text)), replayed.Entries.Select(e => (e.Trainer, e.Trigger, e.Text)));
        Assert.Equal(4242u, GrowthTableService.Load(b.Project).Curves[5].Experience[50]);
        FrontierPokemon set = FrontierService.LoadSets(b.Project, FrontierFacility.Tower).Sets[10];
        Assert.Equal((150, 94), (set.Species, set.Moves[3]));
        Assert.Equal(12, FrontierService.LoadSets(b.Project, FrontierFacility.Stage).Sets[7].Nature);
        Assert.Equal([3, 1, 4, 1, 5], FrontierService.LoadTrainers(b.Project).Trainers[5].SetIds.Select(id => (int)id));
    }

    [Fact]
    public async Task Edits_SurviveSaveRomAndReextract()
    {
        TestProject t = fixture.CreateProject();
        TrainerMessageTable messages = TrainerMessageService.Load(t.Project, t.Metadata);
        messages.Entries[3].Text = "Built into the ROM";
        messages.Entries.Add(new TrainerMessageEntry { Trainer = 905, Trigger = 0, Text = "Hello from a new line" });
        TrainerMessageService.Save(t.Project, t.Metadata, messages);
        GrowthTables growth = GrowthTableService.Load(t.Project);
        growth.Curves[2].Experience[99] = 987654;
        GrowthTableService.Save(t.Project, growth);
        FrontierPokemonSets sets = FrontierService.LoadSets(t.Project, FrontierFacility.Tower);
        sets.Sets[20].Item = 234;
        FrontierService.SaveSets(t.Project, FrontierFacility.Tower, sets);
        FrontierTrainers trainers = FrontierService.LoadTrainers(t.Project);
        trainers.Trainers[8].SetIds.Add(77);
        FrontierService.SaveTrainers(t.Project, trainers);

        string rom = Path.Combine(t.Project.RootPath, "..", "battledata-" + Guid.NewGuid().ToString("N") + ".nds");
        // BuildRomAsync's steps without the script rebuild, which needs the wine bridge.
        var service = new RomExtractionService();
        ToolResult text = await service.RebuildTextArchivesAsync(t.Project, t.Metadata, t.ToolsDirectory);
        Assert.True(text.Succeeded, text.StandardError);
        service.PackTextArchives(t.Project, t.Metadata);
        ToolResult build = await service.BuildAsync(t.Project.RootPath, rom, t.ToolsDirectory);
        Assert.True(build.Succeeded, build.StandardError);
        string extracted = Path.Combine(Path.GetDirectoryName(rom)!, "battledata-reextract-" + Guid.NewGuid().ToString("N"));
        Assert.True((await service.ExtractAsync(rom, extracted, RomProjectFormat.DsRom, t.ToolsDirectory)).Succeeded);
        RomProject.TryOpen(extracted, out RomProject? reopened);

        Assert.Equal(987654u, GrowthTableService.Load(reopened!).Curves[2].Experience[99]);
        Assert.Equal(234, FrontierService.LoadSets(reopened!, FrontierFacility.Tower).Sets[20].Item);
        Assert.Contains((ushort)77, FrontierService.LoadTrainers(reopened!).Trainers[8].SetIds);

        // The re-extracted ROM has the binary text archive; decode it the way a fresh project does.
        await service.PrepareTextArchivesAsync(reopened!, t.Metadata, t.ToolsDirectory);
        TrainerMessageTable fromRom = TrainerMessageService.Load(reopened!, t.Metadata);
        Assert.Equal("Built into the ROM", fromRom.Entries[3].Text);
        Assert.Contains(fromRom.Entries, entry => entry.Trainer == 905 && entry.Trigger == 0 && entry.Text == "Hello from a new line");
    }
}
