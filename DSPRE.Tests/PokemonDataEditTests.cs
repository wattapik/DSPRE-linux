using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class PokemonDataEditTests(PlatinumFixture fixture)
{
    private const int Turtwig = 387;

    [Fact]
    public void PersonalData_EditedFieldsPersist_OthersUntouched()
    {
        RomProject p = fixture.CreateProject().Project;
        PersonalDataRecord before = PersonalDataService.Load(p, Turtwig);

        PersonalDataRecord edited = PersonalDataService.Load(p, Turtwig);
        edited.BaseHP = 200;
        edited.Type2 = PokemonType.Dragon;
        edited.FirstAbility = 66;
        edited.CatchRate = 1;
        edited.EvSpAtk = 3;
        edited.Machines.Add(0);
        PersonalDataService.Save(p, Turtwig, edited);

        PersonalDataRecord after = PersonalDataService.Load(p, Turtwig);
        Assert.Equal(200, after.BaseHP);
        Assert.Equal(PokemonType.Dragon, after.Type2);
        Assert.Equal(66, after.FirstAbility);
        Assert.Equal(1, after.CatchRate);
        Assert.Equal(3, after.EvSpAtk);
        Assert.Contains((byte)0, after.Machines);
        Assert.Equal(before.BaseAtk, after.BaseAtk);
        Assert.Equal(before.Type1, after.Type1);
        Assert.Equal(before.GrowthCurve, after.GrowthCurve);
    }

    [Fact]
    public void PersonalData_UnchangedSaveIsByteIdentical()
    {
        RomProject p = fixture.CreateProject().Project;
        foreach (int id in new[] { 1, Turtwig, 493 })
        {
            string path = Path.Combine(p.PersonalDataDirectory, id.ToString("D4"));
            byte[] original = File.ReadAllBytes(path);
            PersonalDataService.Save(p, id, PersonalDataService.Load(p, id));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void Learnset_AddRemoveAndKeepsSortedOrder()
    {
        RomProject p = fixture.CreateProject().Project;
        LearnsetFile file = LearnsetService.Load(p, Turtwig);
        int count = file.Entries.Count;

        Assert.True(LearnsetService.InsertSorted(file.Entries, new LearnsetEntry(1, 1)));
        Assert.False(LearnsetService.InsertSorted(file.Entries, new LearnsetEntry(1, 1)));
        file.Entries.RemoveAt(file.Entries.Count - 1);
        LearnsetService.Save(p, file);

        LearnsetFile reloaded = LearnsetService.Load(p, Turtwig);
        Assert.Equal(count, reloaded.Entries.Count);
        Assert.Contains(new LearnsetEntry(1, 1), reloaded.Entries);
        Assert.Equal(reloaded.Entries.OrderBy(e => e.Level).ThenBy(e => e.Move).ToList(), reloaded.Entries);
    }

    [Fact]
    public void Learnset_UnchangedSaveIsByteIdentical()
    {
        RomProject p = fixture.CreateProject().Project;
        string path = Path.Combine(p.LearnsetsDirectory, Turtwig.ToString("D4"));
        byte[] original = File.ReadAllBytes(path);
        LearnsetService.Save(p, LearnsetService.Load(p, Turtwig));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Evolution_SlotEditPersists()
    {
        RomProject p = fixture.CreateProject().Project;
        EvolutionFile file = EvolutionService.Load(p, Turtwig);
        file.Slots[0] = new EvolutionSlot { Method = EvolutionMethod.LevelingUp, Param = 5, Target = 1 };
        EvolutionService.Save(p, file);

        EvolutionSlot slot = EvolutionService.Load(p, Turtwig).Slots[0];
        Assert.Equal(EvolutionMethod.LevelingUp, slot.Method);
        Assert.Equal(5, slot.Param);
        Assert.Equal(1, slot.Target);
    }

    [Fact]
    public void Evolution_InvalidSlotsAreDroppedOnSave()
    {
        RomProject p = fixture.CreateProject().Project;
        EvolutionFile file = EvolutionService.Load(p, Turtwig);
        file.Slots[0] = new EvolutionSlot { Method = EvolutionMethod.Trade, Param = 0, Target = 0 };
        EvolutionService.Save(p, file);

        Assert.All(EvolutionService.Load(p, Turtwig).Slots, slot => Assert.Equal(EvolutionMethod.None, slot.Method));
    }

    [Fact]
    public void EggMoves_EditedListPersists()
    {
        TestProject t = fixture.CreateProject();
        List<EggMoveEntry> entries = EggMoveService.Load(t.Project, t.Metadata);
        Assert.NotEmpty(entries);
        entries[0].MoveIds[0] = 1;
        int species = entries[0].SpeciesId;
        int moves = entries[0].MoveIds.Count;
        EggMoveService.Save(t.Project, t.Metadata, entries);

        EggMoveEntry reloaded = EggMoveService.Load(t.Project, t.Metadata).Single(e => e.SpeciesId == species);
        Assert.Equal(moves, reloaded.MoveIds.Count);
        Assert.Equal(1, reloaded.MoveIds[0]);
    }

    [Fact]
    public void MachineMoves_ReplacedMovePersists()
    {
        TestProject t = fixture.CreateProject();
        List<MachineEntry> machines = MachineMoveService.Load(t.Project, t.Metadata);
        ushort old = machines[0].MoveId;
        machines[0].MoveId = (ushort)(old == 1 ? 2 : 1);
        MachineMoveService.Save(t.Project, t.Metadata, machines);

        Assert.Equal(machines[0].MoveId, MachineMoveService.Load(t.Project, t.Metadata)[0].MoveId);
    }
}
