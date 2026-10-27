using Avalonia.Interactivity;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Trainer Editor's DV Calculator button.</summary>
public partial class MainWindow
{
    private async void TrainerDvCalculator_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentTrainer is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        var slots = new List<int>();
        var members = new List<DvCalculatorWindow.Member>();
        for (int i = 0; i < Math.Min(currentTrainer.PartyCount, TrainerPartyRowItems.Count); i++)
        {
            PartyMemberRow row = TrainerPartyRowItems[i];
            if (row.Species < 0) continue;
            string name = row.Species < TrainerPokemonNames.Count ? TrainerPokemonNames[row.Species] : $"#{row.Species}";
            byte genderRatio = PersonalDataService.Load(currentProject, row.Species).GenderRatio;
            members.Add(new DvCalculatorWindow.Member($"{name} Lv. {row.Level}", row.Species, row.Level, genderRatio, row.GenderIndex, row.AbilityIndex, Math.Clamp(row.Difficulty, 0, 255)));
            slots.Add(i);
        }

        if (members.Count == 0)
        {
            TrainerEditorStatus.Text = "This trainer has no Pokémon to calculate natures for.";
            return;
        }

        bool[] classGenders = TrainerPersonality.ReadTrainerClassGenders(currentProject, metadata);
        int trainerClass = currentTrainer.TrainerClass;
        string className = trainerClass < TrainerClassNames.Count ? TrainerClassNames[trainerClass] : $"#{trainerClass}";
        var window = new DvCalculatorWindow(currentProject, metadata, currentTrainer.Id, trainerClass, $"[{currentTrainer.Id}] {currentTrainer.Name}", $"[{trainerClass}] {className}",
            trainerClass >= classGenders.Length || classGenders[trainerClass], members);
        if (await window.ShowDialog<List<DvCalculatorWindow.Member>?>(this) is not { } result) return;

        bool changed = false;
        for (int i = 0; i < result.Count; i++)
        {
            PartyMemberRow old = TrainerPartyRowItems[slots[i]];
            DvCalculatorWindow.Member edited = result[i];
            if (old.Difficulty == edited.Dv && old.GenderIndex == edited.Gender && old.AbilityIndex == edited.Ability) continue;
            // The rows do not raise change notifications for these fields, so a replaced row redraws them.
            var row = new PartyMemberRow
            {
                Index = old.Index, Species = old.Species, Level = old.Level, Difficulty = edited.Dv, BallSeals = old.BallSeals, Form = old.Form,
                GenderIndex = edited.Gender, AbilityIndex = edited.Ability, HeldItem = old.HeldItem,
                Move1 = old.Move1, Move2 = old.Move2, Move3 = old.Move3, Move4 = old.Move4
            };
            TrainerPartyRowItems[slots[i]] = row;
            UpdatePartyRowIcon(row, metadata);
            changed = true;
        }

        if (!changed) return;
        MarkTrainerModified();
        TrainerEditorStatus.Text = "DVs and flags updated from the DV Calculator. Save the trainer to keep them.";
    }
}
