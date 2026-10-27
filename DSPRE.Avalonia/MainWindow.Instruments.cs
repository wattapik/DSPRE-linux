using Avalonia.Controls;
using Avalonia.Interactivity;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Instrument Editor: every instrument bank of the sound archive, each bank's instruments and the sounds (key ranges) they answer with,
/// with their envelope and sample details, and a button to hear one. Viewing only.</summary>
public partial class MainWindow
{
    private IReadOnlyList<MusicService.BankEntry> instrumentBanks = [];
    private IReadOnlyList<NitroSequencer.InstrumentSummary> bankInstruments = [];
    private bool loadingInstruments;

    private sealed record BankRow(MusicService.BankEntry Bank)
    {
        public override string ToString() => $"Bank {Bank.Index}  ({Bank.Sequences.Count} sequence{(Bank.Sequences.Count == 1 ? "" : "s")})";
    }

    private MusicService.BankEntry? SelectedBank => (InstrumentBankList.SelectedItem as BankRow)?.Bank;

    private void ConfigureInstrumentEditor()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (!MusicService.IsSupported(currentProject, metadata))
        {
            InstrumentStatus.Text = "This project has no sound archive.";
            return;
        }

        if (instrumentBanks.Count > 0) return;
        try
        {
            instrumentBanks = MusicService.Banks(currentProject, metadata);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException)
        {
            LogUiError("Sound archive could not be read", ex);
            InstrumentStatus.Text = $"The sound archive could not be read: {ex.Message}";
            return;
        }

        InstrumentBankList.Items.Clear();
        foreach (MusicService.BankEntry bank in instrumentBanks) InstrumentBankList.Items.Add(new BankRow(bank));
        if (InstrumentBankList.Items.Count > 0) InstrumentBankList.SelectedIndex = 0;
    }

    private async void InstrumentBankList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        bankInstruments = [];
        InstrumentList.Items.Clear();
        InstrumentRegionList.Items.Clear();
        InstrumentDetail.Text = "Pick a sound to see its details.";
        if (SelectedBank is not { } bank || currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        InstrumentBankTitle.Text = $"Bank {bank.Index}";
        string used = bank.Sequences.Count == 0 ? "No sequence uses this bank." : $"Used by {string.Join(", ", bank.Sequences.Take(12))}{(bank.Sequences.Count > 12 ? $" and {bank.Sequences.Count - 12} more" : "")}.";
        InstrumentBankInfo.Text = $"Bank file {bank.FileId}; wave archives {string.Join(", ", bank.WaveArchives.Select(id => id is >= 0 and < 0xFFFF ? id.ToString() : "none"))}. {used}";
        InstrumentCount.Text = "Reading...";
        int entrySize = (int)(MusicEntrySize.Value ?? 4);
        try
        {
            IReadOnlyList<NitroSequencer.InstrumentSummary> instruments = await Task.Run(() => MusicService.BankInstruments(currentProject, metadata, bank.Index, entrySize));
            if (SelectedBank != bank) return;
            bankInstruments = instruments;
            FillInstrumentList();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            LogUiError("Instrument bank could not be read", ex);
            InstrumentCount.Text = string.Empty;
            InstrumentStatus.Text = $"The bank could not be read: {ex.Message}";
        }
    }

    private void FillInstrumentList()
    {
        string filter = InstrumentFilter.Text?.Trim() ?? string.Empty;
        InstrumentList.Items.Clear();
        InstrumentRegionList.Items.Clear();
        foreach (NitroSequencer.InstrumentSummary instrument in bankInstruments)
        {
            var row = new InstrumentRow(instrument);
            if (filter.Length == 0 || row.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)) InstrumentList.Items.Add(row);
        }

        InstrumentCount.Text = $"{InstrumentList.Items.Count} of {bankInstruments.Count} instruments";
        if (InstrumentList.Items.Count > 0) InstrumentList.SelectedIndex = 0;
    }

    private void InstrumentFilter_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (bankInstruments.Count > 0) FillInstrumentList();
    }

    private void InstrumentList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        InstrumentRegionList.Items.Clear();
        if (InstrumentList.SelectedItem is not InstrumentRow row) return;
        foreach (NitroSequencer.RegionSummary region in row.Instrument.Regions) InstrumentRegionList.Items.Add(new RegionRow(region));
        if (InstrumentRegionList.Items.Count > 0) InstrumentRegionList.SelectedIndex = 0;
    }

    private void InstrumentRegionList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (InstrumentRegionList.SelectedItem is not RegionRow row) return;
        NitroSequencer.RegionSummary region = row.Region;
        int key = region.Low == 0 && region.High == 127 ? region.BaseNote : region.Low;
        InstrumentNote.Value = Math.Clamp(key, 0, 127);
        string sample = region.Sample is { } wave
            ? $"wave {region.Wave} of archive slot {region.WaveArchive}\n  {wave.Rate} Hz, {(wave.Type switch { 0 => "8-bit PCM", 1 => "16-bit PCM", _ => "ADPCM" })}, {wave.Samples} samples ({wave.Samples / (double)Math.Max(wave.Rate, 1):0.00} s), {(wave.Loops ? "loops" : "plays once")}"
            : region.Kind == "Pulse wave" ? $"duty {region.Wave}" : "no sample";
        InstrumentDetail.Text = $"Keys      {RegionRow.NoteName(region.Low)} to {RegionRow.NoteName(region.High)}  ({region.Low}-{region.High})\n" +
            $"Sound     {region.Kind}\nSample    {sample}\nPlays at  {RegionRow.NoteName(region.BaseNote)} ({region.BaseNote}) as its own pitch\n" +
            $"Envelope  attack {region.Attack}, decay {region.Decay}, sustain {region.Sustain}, release {region.Release}\nPan       {region.Pan} (64 is centre)";
    }

    private void InstrumentNote_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (InstrumentNoteName is not null) InstrumentNoteName.Text = RegionRow.NoteName(Math.Clamp((int)(InstrumentNote.Value ?? 60), 0, 127));
    }

    private async void InstrumentPlay_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (SelectedBank is not { } bank || InstrumentList.SelectedItem is not InstrumentRow row) return;
        int key = (int)(InstrumentNote.Value ?? 60), entrySize = (int)(MusicEntrySize.Value ?? 4);
        try
        {
            MusicService.Rendered note = await Task.Run(() => MusicService.RenderBankNote(currentProject, metadata, bank.Index, row.Instrument.Program, key, 1.0, entrySize));
            string? problem = note.Stereo.Length == 0 ? "That note makes no sound." : AudioPlayer.Play(note.ToWav());
            InstrumentStatus.Text = problem ?? $"Playing instrument {row.Instrument.Program} of bank {bank.Index} at {RegionRow.NoteName(key)}.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            LogUiError("Note could not be played", ex);
            InstrumentStatus.Text = $"The note could not be played: {ex.Message}";
        }
    }

    private void InstrumentStop_Click(object? sender, RoutedEventArgs e)
    {
        AudioPlayer.Stop();
        InstrumentStatus.Text = "Stopped.";
    }
}
