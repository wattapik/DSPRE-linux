using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DSPRE.Core;
using System.Globalization;

namespace DSPRE.Avalonia;

/// <summary>Legacy's "Add Custom Overworld Entry" dialog (DS_Map/Editors/BtxEditor/AddOverworldEntryForm.cs):
/// the new entry's id, the sprite file it shows - or, with an image, the existing sprite whose size and
/// palette the image's new sprite file is based on - and the entry whose behaviour to copy.</summary>
public sealed class AddOverworldEntryWindow : Window
{
    public sealed record SlotOption(uint SpriteId, string Label, int Width, int Height);
    public sealed record Choice(uint EntryId, uint SpriteId, uint CloneFrom, string? ImagePath);

    private readonly IReadOnlyList<SlotOption> slots;
    private readonly ComboBox slotSelector = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox cloneSelector = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox idInput = new();
    private readonly TextBlock imageInfo = new() { Foreground = new SolidColorBrush(Color.Parse("#AAB7C4")), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock slotLabel = new() { Text = "Sprite (this entry's art)" };
    private readonly TextBlock status = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private string? imagePath;
    private (int Width, int Height)? imageSize;

    public AddOverworldEntryWindow(IReadOnlyList<SlotOption> slots, IReadOnlyList<uint> entries, uint? suggestedId)
    {
        this.slots = slots;
        Title = "Add Overworld Entry";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        idInput.Text = suggestedId is uint id ? $"0x{id:X}" : string.Empty;
        cloneSelector.ItemsSource = entries.Select(entry => $"OW Entry {entry} (0x{entry:X})").ToList();
        int defaultClone = entries.ToList().IndexOf(0x78);
        cloneSelector.SelectedIndex = defaultClone >= 0 ? defaultClone : 0;
        RebuildSlots();

        var chooseImage = new Button { Content = "Choose PNG...", Padding = new Thickness(10, 5) };
        var clearImage = new Button { Content = "Clear", Padding = new Thickness(10, 5) };
        chooseImage.Click += async (_, _) =>
        {
            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Pick the new entry's sprite sheet",
                FileTypeFilter = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }]
            });
            if (files.Count != 1) return;
            try
            {
                BattleSpriteService.DecodeImageFileToRgba(files[0].Path.LocalPath, out int width, out int height);
                imagePath = files[0].Path.LocalPath;
                imageSize = (width, height);
                imageInfo.Text = $"{Path.GetFileName(imagePath)}: {width}x{height}. It goes into a new sprite file; pick an existing sprite of the same size below as its template.";
            }
            catch (Exception ex)
            {
                imageInfo.Text = $"That image could not be read: {ex.Message}";
            }

            RebuildSlots();
        };
        clearImage.Click += (_, _) =>
        {
            imagePath = null;
            imageSize = null;
            imageInfo.Text = string.Empty;
            RebuildSlots();
        };

        var add = new Button { Content = "Add", Padding = new Thickness(16, 7) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 7) };
        add.Click += (_, _) => TryAdd(entries);
        cancel.Click += (_, _) => Close(null);

        var imageRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        imageRow.Children.Add(chooseImage);
        imageRow.Children.Add(clearImage);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(add);
        buttons.Children.Add(cancel);
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Needs hzla's PlatPatches overworld sprites expansion and a free custom slot.", Foreground = new SolidColorBrush(Color.Parse("#AAB7C4")), TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                new TextBlock { Text = "Entry id (decimal, or hex with 0x)" },
                idInput,
                new TextBlock { Text = "Image (optional) - choose it first: it decides which sprites below fit" },
                imageRow,
                imageInfo,
                slotLabel,
                slotSelector,
                new TextBlock { Text = "Copy behaviour, shadow and animation from" },
                cloneSelector,
                status,
                buttons
            }
        };
    }

    private bool Fits(SlotOption slot) => imageSize is { } size && slot.Width == size.Width && slot.Height == size.Height;

    private void RebuildSlots()
    {
        uint? previous = slotSelector.SelectedItem is string label ? slots.FirstOrDefault(slot => label.EndsWith(slot.Label, StringComparison.Ordinal))?.SpriteId : null;
        IEnumerable<SlotOption> ordered = imageSize is null ? slots : slots.OrderByDescending(Fits);
        List<SlotOption> list = ordered.ToList();
        slotSelector.ItemsSource = list.Select(slot => (Fits(slot) ? "[fits] " : string.Empty) + slot.Label).ToList();
        slotSelector.Tag = list;
        int keep = previous is uint id ? list.FindIndex(slot => slot.SpriteId == id) : -1;
        slotSelector.SelectedIndex = keep >= 0 ? keep : list.Count > 0 ? 0 : -1;
        slotLabel.Text = imageSize is null ? "Sprite (this entry's art - shared with whatever else shows it)" : "Template sprite (same size; it is copied, never changed)";
    }

    private void TryAdd(IReadOnlyList<uint> entries)
    {
        string text = idInput.Text?.Trim() ?? string.Empty;
        bool parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id)
            : uint.TryParse(text, out id);
        if (!parsed)
        {
            status.Text = "The entry id is not a number.";
            return;
        }

        if (OverworldTableExpansion.CheckEntryId(id) is string problem)
        {
            status.Text = problem;
            return;
        }

        if (entries.Contains(id))
        {
            status.Text = $"Entry 0x{id:X} already exists.";
            return;
        }

        if (slotSelector.Tag is not List<SlotOption> list || slotSelector.SelectedIndex < 0 || cloneSelector.SelectedIndex < 0)
        {
            status.Text = "Pick a sprite and an entry to copy from.";
            return;
        }

        SlotOption slot = list[slotSelector.SelectedIndex];
        if (imageSize is { } size && !Fits(slot))
        {
            status.Text = $"The image is {size.Width}x{size.Height}; pick a template sprite of that size (marked [fits]).";
            return;
        }

        Close(new Choice(id, slot.SpriteId, entries[cloneSelector.SelectedIndex], imagePath));
    }
}
