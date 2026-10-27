using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class MoveItemTrainerEditTests(PlatinumFixture fixture)
{
    [Fact]
    public void Move_EditedFieldsPersist()
    {
        RomProject p = fixture.CreateProject().Project;
        const int tackle = 33;
        MoveRecord move = MoveDataService.Load(p, tackle);
        byte accuracy = move.Accuracy;
        move.Damage = 250;
        move.Pp = 99;
        move.MoveType = PokemonType.Ghost;
        move.Split = MoveSplit.Special;
        move.Priority = -3;
        MoveDataService.Save(p, tackle, move);

        MoveRecord after = MoveDataService.Load(p, tackle);
        Assert.Equal(250, after.Damage);
        Assert.Equal(99, after.Pp);
        Assert.Equal(PokemonType.Ghost, after.MoveType);
        Assert.Equal(MoveSplit.Special, after.Split);
        Assert.Equal(-3, after.Priority);
        Assert.Equal(accuracy, after.Accuracy);
    }

    [Fact]
    public void Move_UnchangedSaveIsByteIdentical()
    {
        RomProject p = fixture.CreateProject().Project;
        foreach (int id in new[] { 1, 33, 300 })
        {
            string path = Path.Combine(p.MoveDataDirectory, id.ToString("D4"));
            byte[] original = File.ReadAllBytes(path);
            MoveDataService.Save(p, id, MoveDataService.Load(p, id));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void Item_EditedFieldsPersist()
    {
        TestProject t = fixture.CreateProject();
        ItemTableEntry entry = ItemDataService.ReadTableEntry(t.Project, t.Metadata, 17);
        ItemRecord item = ItemDataService.Load(t.Project, entry.ItemDataId);
        item.Price = 12345;
        item.FlingPower = 77;
        item.PreventToss = !item.PreventToss;
        bool toss = item.PreventToss;
        ItemDataService.Save(t.Project, entry.ItemDataId, item);

        ItemRecord after = ItemDataService.Load(t.Project, entry.ItemDataId);
        Assert.Equal(12345, after.Price);
        Assert.Equal(77, after.FlingPower);
        Assert.Equal(toss, after.PreventToss);
    }

    [Fact]
    public void Item_ResaveIsStable()
    {
        RomProject p = fixture.CreateProject().Project;
        foreach (int id in ItemDataService.GetIds(p).Take(40))
        {
            string path = Path.Combine(p.ItemDataDirectory, id.ToString("D4"));
            ItemDataService.Save(p, id, ItemDataService.Load(p, id));
            byte[] first = File.ReadAllBytes(path);
            ItemDataService.Save(p, id, ItemDataService.Load(p, id));
            Assert.Equal(first, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void ItemTable_EntryRewritePersists()
    {
        TestProject t = fixture.CreateProject();
        ItemTableEntry entry = ItemDataService.ReadTableEntry(t.Project, t.Metadata, 5);
        ItemTableEntry edited = entry with { ItemDataId = (ushort)(entry.ItemDataId + 1) };
        ItemDataService.WriteTableEntry(t.Project, t.Metadata, 5, edited);
        Assert.Equal(edited, ItemDataService.ReadTableEntry(t.Project, t.Metadata, 5));
    }

    [Fact]
    public void Trainer_PartyEditPersists()
    {
        TestProject t = fixture.CreateProject();
        int id = TrainerService.GetIds(t.Project).First(i => i > 0);
        TrainerRecord trainer = TrainerService.Load(t.Project, id, t.Metadata, "Test");
        trainer.PartyCount = 1;
        trainer.ChooseItems = true;
        trainer.DoubleBattle = true;
        trainer.Ai[3] = true;
        trainer.Party[0].Species = 25;
        trainer.Party[0].Level = 77;
        trainer.Party[0].HeldItem = 17;
        TrainerService.Save(t.Project, trainer, t.Metadata);

        TrainerRecord after = TrainerService.Load(t.Project, id, t.Metadata, "Test");
        Assert.Equal(1, after.PartyCount);
        Assert.True(after.DoubleBattle);
        Assert.True(after.Ai[3]);
        Assert.Equal(25, after.Party[0].Species);
        Assert.Equal(77, after.Party[0].Level);
        Assert.Equal(17, after.Party[0].HeldItem);
    }

    [Fact]
    public void Trainer_UnchangedSaveIsByteIdenticalForRealTrainers()
    {
        TestProject t = fixture.CreateProject();
        foreach (int id in TrainerService.GetIds(t.Project).Where(i => i > 0).Take(25))
        {
            string props = Path.Combine(t.Project.TrainerPropertiesDirectory, id.ToString("D4"));
            string party = Path.Combine(t.Project.TrainerPartyDirectory, id.ToString("D4"));
            byte[] p0 = File.ReadAllBytes(props), q0 = File.ReadAllBytes(party);
            TrainerService.Save(t.Project, TrainerService.Load(t.Project, id, t.Metadata, "Test"), t.Metadata);
            Assert.Equal(p0, File.ReadAllBytes(props));
            Assert.Equal(q0, File.ReadAllBytes(party));
        }
    }

    [Fact]
    public void Trade_EditPersists()
    {
        TestProject t = fixture.CreateProject();
        int id = TradeDataService.GetIds(t.Project).First();
        TradeRecord trade = TradeDataService.Load(t.Project, id, isHgss: false);
        trade.Species = 150;
        trade.HpIv = 31;
        trade.RequestedSpecies = 25;
        TradeDataService.Save(t.Project, id, trade, isHgss: false);

        TradeRecord after = TradeDataService.Load(t.Project, id, isHgss: false);
        Assert.Equal(150, after.Species);
        Assert.Equal(31, after.HpIv);
        Assert.Equal(25, after.RequestedSpecies);
    }
}
