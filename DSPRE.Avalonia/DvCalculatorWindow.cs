using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Legacy's DV Calculator (DS_Map/Editors/TrainerEditor/DVCalculator/DVCalc.cs): shows the nature
/// each party member of a trainer ends up with, and finds the DV giving the nature wanted. Closes with
/// the edited members, or null when cancelled.</summary>
public sealed class DvCalculatorWindow : Window
{
    public sealed record Member(string Label, int Species, int Level, byte GenderRatio, int Gender, int Ability, int Dv);

    private static readonly string[] GenderChoices = ["No Flag", "Force Male", "Force Female"];
    private static readonly string[] AbilityChoices = ["No Flag", "Force Ability 1", "Force Ability 2"];
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#AAB7C4"));
    private const string MoreInfoUrl = "https://gist.github.com/YakosWG/2200732c473656db2e47c37ca72807d7";

    private readonly RomProject project;
    private readonly RomGameMetadata metadata;
    private readonly int trainerId;
    private readonly int trainerClass;
    private readonly List<Member> members;
    private readonly RadioButton maleButton;
    private readonly List<(ComboBox Gender, ComboBox Ability, NumericUpDown Dv, TextBlock Iv, TextBlock Nature)> rows = [];

    public DvCalculatorWindow(RomProject project, RomGameMetadata metadata, int trainerId, int trainerClass, string trainerLabel, string classLabel,
        bool classIsMale, IReadOnlyList<Member> members)
    {
        this.project = project;
        this.metadata = metadata;
        this.trainerId = trainerId;
        this.trainerClass = trainerClass;
        this.members = members.ToList();
        bool flagsApply = TrainerPersonality.FlagsApply(project, metadata);

        Title = "DV Calculator";
        Width = 1020;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        maleButton = new RadioButton { Content = "Male or Multi", GroupName = "DvTrainerGender", IsChecked = classIsMale, Margin = new Thickness(0, 0, 12, 0) };
        var femaleButton = new RadioButton { Content = "Female", GroupName = "DvTrainerGender", IsChecked = !classIsMale };
        maleButton.IsCheckedChanged += (_, _) => UpdateNatures();
        ToolTip.SetTip(maleButton, "The trainer class's gender from the game's table. Change it here only if you moved that table: it is used for this calculation only and is not saved.");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,130,150,130,30,170,Auto,Auto"), ColumnSpacing = 8, RowSpacing = 6 };
        string[] headers = ["Pokémon", "Gender flag", "Ability flag", "DV", "IV", "Nature", "", ""];
        for (int c = 0; c < headers.Length; c++) grid.Children.Add(Cell(new TextBlock { Text = headers[c], Foreground = MutedBrush, FontSize = 12 }, 0, c));

        for (int i = 0; i < this.members.Count; i++)
        {
            Member member = this.members[i];
            int slot = i;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var gender = new ComboBox { ItemsSource = GenderChoices, SelectedIndex = Math.Clamp(member.Gender, 0, 2), IsEnabled = flagsApply, HorizontalAlignment = HorizontalAlignment.Stretch };
            var ability = new ComboBox { ItemsSource = AbilityChoices, SelectedIndex = Math.Clamp(member.Ability, 0, 2), IsEnabled = flagsApply, HorizontalAlignment = HorizontalAlignment.Stretch };
            var dv = new NumericUpDown { Minimum = 0, Maximum = 255, Value = member.Dv, FormatString = "0", Increment = 1 };
            var iv = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            var nature = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            var change = new Button { Content = "Change...", Padding = new Thickness(10, 5) };
            var showAll = new Button { Content = "Show All...", Padding = new Thickness(10, 5) };
            ToolTip.SetTip(gender, "Gender flags can change the ability too: pick an ability flag as well to prevent that.");
            ToolTip.SetTip(change, "Pick a nature: each is listed with the highest DV that gives it.");
            ToolTip.SetTip(showAll, "Every DV with the nature and IVs it gives.");
            gender.SelectionChanged += (_, _) => UpdateNatures();
            ability.SelectionChanged += (_, _) => UpdateNatures();
            dv.ValueChanged += (_, _) => UpdateNatures();
            change.Click += async (_, _) => await PickDvAsync(slot, highestOnly: true);
            showAll.Click += async (_, _) => await PickDvAsync(slot, highestOnly: false);

            int row = i + 1;
            grid.Children.Add(Cell(new TextBlock { Text = member.Label, VerticalAlignment = VerticalAlignment.Center }, row, 0));
            grid.Children.Add(Cell(gender, row, 1));
            grid.Children.Add(Cell(ability, row, 2));
            grid.Children.Add(Cell(dv, row, 3));
            grid.Children.Add(Cell(iv, row, 4));
            grid.Children.Add(Cell(nature, row, 5));
            grid.Children.Add(Cell(change, row, 6));
            grid.Children.Add(Cell(showAll, row, 7));
            rows.Add((gender, ability, dv, iv, nature));
        }

        grid.RowDefinitions.Insert(0, new RowDefinition(GridLength.Auto));

        var apply = new Button { Content = "Apply to Party", Padding = new Thickness(14, 7) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 7) };
        var moreInfo = new Button { Content = "How Trainer PIDs Work...", Padding = new Thickness(14, 7) };
        apply.Click += (_, _) => Close(Result());
        cancel.Click += (_, _) => Close(null);
        moreInfo.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri(MoreInfoUrl));
        };

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"Trainer {trainerLabel}   ·   Class {classLabel}", FontWeight = FontWeight.SemiBold },
                new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = "Trainer gender:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }, maleButton, femaleButton } },
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Foreground = MutedBrush, FontSize = 12,
                    Text = "A trainer Pokémon's DV (difficulty value, 0-255) sets all six IVs to DV × 31 / 255, and together with the trainer, species and level it fixes the nature. " +
                           "Use Change to find the highest DV giving a nature, or Show All for every combination. " +
                           (flagsApply
                               ? "Gender and ability flags change the result for that Pokémon and every one after it, so work from the top down."
                               : "This game has no gender or ability flags (they need HeartGold/SoulSilver or Platinum with the AI backport).")
                },
                grid,
                new DockPanel
                {
                    Children =
                    {
                        moreInfo,
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { apply, cancel } }
                    }
                }
            }
        };

        UpdateNatures();
    }

    private static Control Cell(Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        return control;
    }

    private int DvOf(int slot) => (int)(rows[slot].Dv.Value ?? 0);

    /// <summary>A calculator positioned at <paramref name="slot"/>: earlier members have been generated,
    /// since their flags change what later ones get.</summary>
    private TrainerPersonality PersonalityAt(int slot)
    {
        var personality = new TrainerPersonality(project, metadata, maleButton.IsChecked == true);
        for (int i = 0; i < slot; i++) Next(personality, i, DvOf(i));
        return personality;
    }

    private uint Next(TrainerPersonality personality, int slot, int dv) =>
        personality.NextPid(trainerId, trainerClass, members[slot].Species, (byte)members[slot].Level, members[slot].GenderRatio,
            rows[slot].Gender.SelectedIndex, rows[slot].Ability.SelectedIndex, (byte)dv);

    private void UpdateNatures()
    {
        if (rows.Count < members.Count) return;
        TrainerPersonality personality = PersonalityAt(0);
        for (int i = 0; i < members.Count; i++)
        {
            uint pid = Next(personality, i, DvOf(i));
            rows[i].Iv.Text = (DvOf(i) * 31 / 255).ToString();
            rows[i].Nature.Text = TrainerPersonality.Natures[TrainerPersonality.NatureOf(pid)];
        }
    }

    private async Task PickDvAsync(int slot, bool highestOnly)
    {
        List<(int Dv, int Iv, int Nature)> natures = PersonalityAt(slot).AllNatures(trainerId, trainerClass, members[slot].Species, (byte)members[slot].Level,
            members[slot].GenderRatio, rows[slot].Gender.SelectedIndex, rows[slot].Ability.SelectedIndex);
        // AllNatures runs from DV 255 down, so the first of each nature is its highest DV.
        if (highestOnly) natures = natures.GroupBy(n => n.Nature).Select(group => group.First()).OrderBy(n => TrainerPersonality.Natures[n.Nature]).ToList();

        var list = new ListBox
        {
            ItemsSource = natures.Select(n => $"{TrainerPersonality.Natures[n.Nature],-22}  DV {n.Dv,3}  IV {n.Iv,2}").ToList(),
            FontFamily = new FontFamily("monospace"),
            Height = 420
        };
        var picker = new Window
        {
            Title = highestOnly ? $"{members[slot].Label}: highest DV for each nature" : $"{members[slot].Label}: every DV",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Foreground = Foreground
        };
        var use = new Button { Content = "Use This DV", Padding = new Thickness(14, 7) };
        var close = new Button { Content = "Cancel", Padding = new Thickness(14, 7) };
        use.Click += (_, _) => picker.Close((int?)list.SelectedIndex);
        close.Click += (_, _) => picker.Close(null);
        list.DoubleTapped += (_, _) => picker.Close((int?)list.SelectedIndex);
        picker.Content = new StackPanel
        {
            Margin = new Thickness(14),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Double-click a line, or pick one and choose Use This DV.", Foreground = MutedBrush, FontSize = 12 },
                list,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { use, close } }
            }
        };

        if (await picker.ShowDialog<int?>(this) is int picked && picked >= 0 && picked < natures.Count) rows[slot].Dv.Value = natures[picked].Dv;
    }

    private List<Member> Result() =>
        members.Select((member, i) => member with { Gender = rows[i].Gender.SelectedIndex, Ability = rows[i].Ability.SelectedIndex, Dv = DvOf(i) }).ToList();
}
