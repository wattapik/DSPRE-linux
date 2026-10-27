using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DSPRE.Core;
using System.IO;

namespace DSPRE.Avalonia;

/// <summary>The Music Editor: lists the game's sequences (music, jingles, sound effects), plays them with <see cref="NitroSequencer"/> and
/// exports them as WAV files or as the game's own sequence, bank and wave files.</summary>
public partial class MainWindow
{
    private IReadOnlyList<MusicService.Entry> musicEntries = [];
    private MusicService.Rendered? lastMusic;
    private int lastMusicIndex = -1;
    private int lastMusicEntrySize = 4;
    private bool loadingMusic;

    private void ConfigureMusicEditor()
    {
        MusicRoll.NoteClicked -= MusicRoll_NoteClicked;
        MusicRoll.NoteClicked += MusicRoll_NoteClicked;
        MusicRoll.PlayheadDragged -= MusicRoll_PlayheadDragged;
        MusicRoll.PlayheadDragged += MusicRoll_PlayheadDragged;
        MusicRoll.PlayheadReleased -= MusicRoll_PlayheadReleased;
        MusicRoll.PlayheadReleased += MusicRoll_PlayheadReleased;
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (!MusicService.IsSupported(currentProject, metadata))
        {
            MusicStatus.Text = "This project has no sound archive.";
            return;
        }

        if (musicEntries.Count == 0)
        {
            try
            {
                musicEntries = MusicService.List(currentProject, metadata);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException)
            {
                LogUiError("Sound archive could not be read", ex);
                MusicStatus.Text = $"The sound archive could not be read: {ex.Message}";
                return;
            }
        }

        FillMusicList();
    }

    private void FillMusicList()
    {
        string filter = MusicFilter.Text?.Trim() ?? string.Empty;
        string category = (MusicCategory.SelectedItem as ComboBoxItem)?.Content as string ?? "All";
        loadingMusic = true;
        try
        {
            MusicList.Items.Clear();
            foreach (MusicService.Entry entry in musicEntries)
            {
                if (category != "All" && entry.Category != category) continue;
                if (filter.Length > 0 && !entry.Info.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                MusicList.Items.Add(entry);
            }
        }
        finally
        {
            loadingMusic = false;
        }
    }

    private void MusicFilter_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (musicEntries.Count > 0) FillMusicList();
    }

    private void MusicCategory_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (musicEntries.Count > 0) FillMusicList();
    }

    private MusicService.Entry? SelectedMusic => MusicList.SelectedItem as MusicService.Entry;

    private void MusicList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingMusic) return;
        lastMusic = null;
        if (SelectedMusic is not { } entry)
        {
            MusicTitle.Text = "Pick a sequence";
            MusicInfo.Text = string.Empty;
            return;
        }

        UpdateMusicEditState();
        _ = LoadMusicInstrumentsAsync(entry);
        _ = LoadMusicNotesAsync(entry);
    }

    private NitroSequencer.SequenceView? musicView;

    private static string Position(int tick) => $"{tick / (PianoRollView.TicksPerBeat * PianoRollView.BeatsPerBar) + 1}.{tick / PianoRollView.TicksPerBeat % PianoRollView.BeatsPerBar + 1}.{tick % PianoRollView.TicksPerBeat:D2}";

    private async Task LoadMusicNotesAsync(MusicService.Entry entry)
    {
        StopMusicPlayhead();
        musicView = null;
        MusicRoll.View = null;
        MusicRoll.PlayheadTick = 0;
        MusicRoll.SelectedOffset = -1;
        MusicTracks.Items.Clear();
        MusicNoteList.Items.Clear();
        MusicRollInfo.Text = "Reading notes...";
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        var options = new NitroSequencer.Options((double)(MusicSeconds.Value ?? 120), (int)(MusicLoops.Value ?? 0), (int)(MusicEntrySize.Value ?? 4));
        byte[]? edited = pendingSequences.TryGetValue(entry.Info.Index, out PendingSequence? pending) ? pending.File : null;
        try
        {
            NitroSequencer.SequenceView view = await Task.Run(() => MusicService.Notes(currentProject, metadata, entry.Info.Index, options, edited));
            if (SelectedMusic != entry) return;
            ShowMusicNotes(view);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            LogUiError("Notes could not be read", ex);
            MusicRollInfo.Text = $"The notes could not be read: {ex.Message}";
        }
    }

    private void ShowMusicNotes(NitroSequencer.SequenceView view)
    {
        double playhead = MusicRoll.PlayheadTick;
        int selectedTrackNumber = MusicTracks.SelectedItem is TrackRow current ? current.Track : -1;
        musicView = view;
        MusicRoll.View = view;
        MusicRoll.PlayheadTick = Math.Min(playhead, view.EndTick);
        MusicRollInfo.Text = $"{view.Notes.Count} notes on {view.TrackCount} tracks, {view.EndTick / (PianoRollView.TicksPerBeat * PianoRollView.BeatsPerBar) + 1} bars";
        MusicTracks.Items.Clear();
        MusicTracks.Items.Add("All tracks");
        foreach (IGrouping<int, NitroSequencer.NoteEvent> track in view.Notes.GroupBy(note => note.Track).OrderBy(group => group.Key))
        {
            string programs = string.Join(", ", track.Select(note => note.Program).Distinct().Order());
            MusicTracks.Items.Add(new TrackRow(track.Key, $"Track {track.Key + 1}: instrument {programs}, {track.Count()} notes, {PianoRollView.NoteName(track.Min(note => note.Key))} to {PianoRollView.NoteName(track.Max(note => note.Key))}"));
        }

        int row = MusicTracks.Items.OfType<TrackRow>().ToList().FindIndex(track => track.Track == selectedTrackNumber);
        MusicTracks.SelectedIndex = row >= 0 ? row + 1 : 0;
        if (reselectNote is { } wanted) SelectMusicNote(wanted.Offset, wanted.Start);
        reselectNote = null;
        UpdateMusicEditState();
    }

    private sealed record TrackRow(int Track, string Text)
    {
        public override string ToString() => Text;
    }

    private void MusicRollZoom_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (MusicRoll is not null) MusicRoll.PixelsPerTick = e.NewValue;
    }

    private void MusicTracks_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        MusicNoteList.Items.Clear();
        int track = MusicTracks.SelectedItem is TrackRow row ? row.Track : -1;
        MusicRoll.SelectedTrack = track;
        if (musicView is null) return;
        const int limit = 4000;
        IEnumerable<NitroSequencer.NoteEvent> notes = musicView.Notes.Where(note => track < 0 || note.Track == track).OrderBy(note => note.Start).ThenBy(note => note.Track);
        int shown = 0;
        foreach (NitroSequencer.NoteEvent note in notes)
        {
            if (shown++ == limit)
            {
                MusicNoteList.Items.Add($"... and {notes.Count() - limit} more");
                break;
            }

            MusicNoteList.Items.Add(new NoteRow(note, $"{Position(note.Start),-9} T{note.Track + 1,-2} {PianoRollView.NoteName(note.Key),-4} len {note.Length,-4} vel {note.Velocity,-3} instr {note.Program}"));
        }
    }

    private sealed record InstrumentRow(NitroSequencer.InstrumentSummary Instrument)
    {
        public override string ToString() => $"{Instrument.Program,3}   {Instrument.Layout}   ({Instrument.Regions.Count} sound{(Instrument.Regions.Count == 1 ? "" : "s")}: {string.Join(", ", Instrument.Regions.Select(region => region.Kind).Distinct())})";
    }

    private sealed record RegionRow(NitroSequencer.RegionSummary Region)
    {
        private static readonly string[] NoteNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

        public static string NoteName(int key) => $"{NoteNames[key % 12]}{key / 12 - 1}";

        public override string ToString()
        {
            string keys = Region.Low == Region.High ? NoteName(Region.Low) : $"{NoteName(Region.Low)}-{NoteName(Region.High)}";
            string sample = Region.Sample is { } wave
                ? $"wave {Region.Wave} of archive slot {Region.WaveArchive}: {wave.Rate} Hz {(wave.Type switch { 0 => "8-bit", 1 => "16-bit", _ => "ADPCM" })}, {wave.Samples} samples{(wave.Loops ? ", loops" : string.Empty)}, plays at {NoteName(Region.BaseNote)}"
                : Region.Kind == "Pulse wave" ? $"duty {Region.Wave}" : string.Empty;
            return $"{keys,-9} {Region.Kind}  {sample}  A{Region.Attack} D{Region.Decay} S{Region.Sustain} R{Region.Release} pan {Region.Pan}";
        }
    }

    private async Task LoadMusicInstrumentsAsync(MusicService.Entry entry)
    {
        MusicInstruments.Items.Clear();
        MusicRegions.Items.Clear();
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        int entrySize = (int)(MusicEntrySize.Value ?? 4);
        try
        {
            IReadOnlyList<NitroSequencer.InstrumentSummary> instruments = await Task.Run(() => MusicService.Instruments(currentProject, metadata, entry.Info.Index, entrySize));
            if (SelectedMusic != entry) return;
            foreach (NitroSequencer.InstrumentSummary instrument in instruments) MusicInstruments.Items.Add(new InstrumentRow(instrument));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException)
        {
            LogUiError("Instruments could not be read", ex);
            MusicStatus.Text = $"The instruments could not be read: {ex.Message}";
        }
    }

    private void MusicInstruments_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        MusicRegions.Items.Clear();
        if (MusicInstruments.SelectedItem is not InstrumentRow row) return;
        foreach (NitroSequencer.RegionSummary region in row.Instrument.Regions) MusicRegions.Items.Add(new RegionRow(region));
        if (MusicRegions.Items.Count > 0) MusicRegions.SelectedIndex = 0;
    }

    private void MusicRegions_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (MusicRegions.SelectedItem is not RegionRow row) return;
        int key = row.Region.Low == 0 && row.Region.High == 127 ? row.Region.BaseNote : row.Region.Low;
        MusicNote.Value = Math.Clamp(key, 0, 127);
        MusicNoteName.Text = RegionRow.NoteName(Math.Clamp(key, 0, 127));
    }

    private async void MusicPlayNote_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (SelectedMusic is not { } entry || MusicInstruments.SelectedItem is not InstrumentRow row) return;
        int key = (int)(MusicNote.Value ?? 60), entrySize = (int)(MusicEntrySize.Value ?? 4);
        MusicNoteName.Text = RegionRow.NoteName(Math.Clamp(key, 0, 127));
        try
        {
            MusicService.Rendered note = await Task.Run(() => MusicService.RenderNote(currentProject, metadata, entry.Info.Index, row.Instrument.Program, key, 1.0, entrySize));
            string? problem = note.Stereo.Length == 0 ? "That note makes no sound." : AudioPlayer.Play(note.ToWav());
            MusicStatus.Text = problem ?? $"Playing instrument {row.Instrument.Program} at {RegionRow.NoteName(key)}.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            LogUiError("Note could not be played", ex);
            MusicStatus.Text = $"The note could not be played: {ex.Message}";
        }
    }

    private async Task<MusicService.Rendered?> RenderSelectedMusicAsync()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null || SelectedMusic is not { } entry) return null;
        if (lastMusic is not null && lastMusicIndex == entry.Info.Index && lastMusicEntrySize == (int)(MusicEntrySize.Value ?? 4)) return lastMusic;
        var options = new NitroSequencer.Options((double)(MusicSeconds.Value ?? 120), (int)(MusicLoops.Value ?? 0), (int)(MusicEntrySize.Value ?? 4));
        BeginBlockingLoad($"Rendering {entry.Info.Name}...", showImmediately: true);
        try
        {
            byte[]? edited = pendingSequences.TryGetValue(entry.Info.Index, out PendingSequence? pending) ? pending.File : null;
            MusicService.Rendered rendered = await Task.Run(() => MusicService.Render(currentProject, metadata, entry.Info.Index, options, edited));
            lastMusic = rendered;
            lastMusicIndex = entry.Info.Index;
            lastMusicEntrySize = options.BankEntrySize;
            return rendered;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or IndexOutOfRangeException or ArgumentException)
        {
            LogUiError("Sequence could not be played", ex);
            MusicStatus.Text = $"{entry.Info.Name} could not be played: {ex.Message}";
            return null;
        }
        finally
        {
            EndBlockingLoad();
        }
    }

    private async void MusicPlay_Click(object? sender, RoutedEventArgs e)
    {
        lastMusic = null;
        if (await RenderSelectedMusicAsync() is not { } rendered) return;
        if (rendered.Stereo.Length == 0)
        {
            MusicStatus.Text = "That sequence makes no sound.";
            return;
        }

        double from = musicView is null ? 0 : TickToSeconds(MusicRoll.PlayheadTick);
        await StartMusicAsync(rendered, from);
    }

    private async Task StartMusicAsync(MusicService.Rendered rendered, double fromSeconds)
    {
        int skip = Math.Clamp((int)(fromSeconds * rendered.SampleRate), 0, rendered.Stereo.Length / 2) * 2;
        MusicService.Rendered part = skip == 0 ? rendered : rendered with { Stereo = rendered.Stereo[skip..] };
        if (part.Stereo.Length == 0)
        {
            MusicStatus.Text = "The play line is past the end of the sound.";
            return;
        }

        string? problem = await Task.Run(() => AudioPlayer.Play(part.ToWav()));
        MusicStatus.Text = problem ?? $"Playing {SelectedMusic?.Info.Name} ({part.Seconds:0.0} s).";
        if (problem is null && musicView is not null) StartMusicPlayhead(fromSeconds, part.Seconds);
        else StopMusicPlayhead();
    }

    private readonly global::Avalonia.Threading.DispatcherTimer musicPlayheadTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly System.Diagnostics.Stopwatch musicClock = new();
    private double musicPlayFrom, musicPlayLength, musicStartTick;
    private bool musicPlayheadWired;

    private double TickToSeconds(double tick)
    {
        double seconds = 0, tempo = 120, at = 0;
        foreach (NitroSequencer.TempoEvent change in musicView!.Tempos.OrderBy(change => change.Tick))
        {
            if (change.Tick >= tick) break;
            seconds += (change.Tick - at) * 60.0 / (tempo * PianoRollView.TicksPerBeat);
            at = change.Tick;
            tempo = change.Tempo;
        }

        return seconds + (tick - at) * 60.0 / (tempo * PianoRollView.TicksPerBeat);
    }

    private double SecondsToTick(double target)
    {
        double seconds = 0, tempo = 120, at = 0;
        foreach (NitroSequencer.TempoEvent change in musicView!.Tempos.OrderBy(change => change.Tick))
        {
            double reach = seconds + (change.Tick - at) * 60.0 / (tempo * PianoRollView.TicksPerBeat);
            if (reach >= target) break;
            seconds = reach;
            at = change.Tick;
            tempo = change.Tempo;
        }

        return at + (target - seconds) * tempo * PianoRollView.TicksPerBeat / 60.0;
    }

    private void StartMusicPlayhead(double fromSeconds, double length)
    {
        if (!musicPlayheadWired)
        {
            musicPlayheadWired = true;
            musicPlayheadTimer.Tick += MusicPlayhead_Tick;
        }

        musicStartTick = MusicRoll.PlayheadTick;
        musicPlayFrom = fromSeconds;
        musicPlayLength = length;
        musicClock.Restart();
        musicPlayheadTimer.Start();
    }

    private void StopMusicPlayhead()
    {
        musicPlayheadTimer.Stop();
        musicClock.Reset();
    }

    private void MusicPlayhead_Tick(object? sender, EventArgs e)
    {
        if (musicView is null)
        {
            StopMusicPlayhead();
            return;
        }

        double elapsed = musicClock.Elapsed.TotalSeconds;
        if (elapsed >= musicPlayLength)
        {
            StopMusicPlayhead();
            MusicRoll.PlayheadTick = musicStartTick;
            return;
        }

        MusicRoll.PlayheadTick = Math.Min(SecondsToTick(musicPlayFrom + elapsed), musicView.EndTick);
        double x = MusicRoll.PlayheadX;
        double left = MusicRollScroll.Offset.X, width = MusicRollScroll.Viewport.Width;
        if (x < left || x > left + width - 40) MusicRollScroll.Offset = MusicRollScroll.Offset.WithX(Math.Max(x - 60, 0));
    }

    private void MusicRoll_PlayheadDragged(double tick)
    {
        if (musicClock.IsRunning)
        {
            musicStopWasPlaying = true;
            AudioPlayer.Stop();
            StopMusicPlayhead();
        }
    }

    private async void MusicRoll_PlayheadReleased(double tick)
    {
        if (musicView is null) return;
        bool wasPlaying = musicStopWasPlaying;
        if (!wasPlaying) return;
        musicStopWasPlaying = false;
        if (await RenderSelectedMusicAsync() is { } rendered) await StartMusicAsync(rendered, TickToSeconds(tick));
    }

    private bool musicStopWasPlaying;

    private void MusicVolume_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        AudioPlayer.Volume = e.NewValue / 100;
        if (MusicVolumeText is not null) MusicVolumeText.Text = $"{e.NewValue:0}%";
    }

    private void MusicStop_Click(object? sender, RoutedEventArgs e)
    {
        AudioPlayer.Stop();
        MusicRoll.PlayheadTick = musicClock.IsRunning ? musicStartTick : MusicRoll.PlayheadTick;
        StopMusicPlayhead();
        MusicStatus.Text = "Stopped.";
    }

    private async void MusicExportWav_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedMusic is not { } entry) return;
        if (await RenderSelectedMusicAsync() is not { } rendered) return;
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save sound",
            SuggestedFileName = entry.Info.Name + ".wav",
            DefaultExtension = "wav",
            FileTypeChoices = [new FilePickerFileType("WAV audio") { Patterns = ["*.wav"] }]
        });
        if (file is null) return;
        await File.WriteAllBytesAsync(file.Path.LocalPath, rendered.ToWav());
        MusicStatus.Text = $"Saved {file.Name} ({rendered.Seconds:0.0} s).";
    }

    private async void MusicExportFiles_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null || SelectedMusic is not { } entry) return;
        string? folder = await PickFolderAsync("Pick the folder for the sequence, bank and wave files");
        if (folder is null) return;
        try
        {
            IReadOnlyList<string> written = await Task.Run(() => MusicService.ExportFiles(currentProject, metadata, entry.Info.Index, folder));
            MusicStatus.Text = $"Saved {written.Count} files to {folder}: {string.Join(", ", written.Select(Path.GetFileName))}.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException)
        {
            LogUiError("Sequence files could not be exported", ex);
            MusicStatus.Text = $"The files could not be exported: {ex.Message}";
        }
    }

    private sealed class PendingSequence(byte[] file)
    {
        public byte[] File = file;
        public readonly List<SequenceEditor.NoteOp> Operations = [];
    }

    private sealed record NoteRow(NitroSequencer.NoteEvent Note, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly Dictionary<int, PendingSequence> pendingSequences = [];
    private (int Offset, int Start)? reselectNote;
    private bool loadingNoteEdit;

    private NitroSequencer.NoteEvent? SelectedNote => (MusicNoteList.SelectedItem as NoteRow)?.Note;

    private NitroSequencer.NoteEvent? shownNote;
    private (int Track, NitroSequencer.NoteEvent Reference)? creatingNote;
    private bool creatingTouched;
    private bool switchingNotes;

    private void UpdateMusicEditState()
    {
        MusicEntryInfoRefresh();
        NitroSequencer.NoteEvent? note = creatingNote is null ? SelectedNote : null;
        shownNote = note;
        bool pending = SelectedMusic is { } entry && pendingSequences.ContainsKey(entry.Info.Index);
        bool editing = note is not null || creatingNote is not null;
        foreach (Control control in new Control[] { MusicEditKey, MusicEditVelocity, MusicEditLength, MusicEditStart, MusicEditProgram, MusicEditApply }) control.IsEnabled = editing;
        MusicEditDelete.IsEnabled = note is not null;
        MusicNewNote.IsEnabled = musicView is not null;
        MusicEditSave.IsEnabled = pending;
        MusicEditDiscard.IsEnabled = pending;
        MusicRoll.SelectedOffset = note?.Offset ?? -1;
        MusicRoll.ClearPreview();
        if (creatingNote is { } creating)
        {
            MusicNoteEditTitle.Text = $"New note on track {creating.Track + 1} (press Apply to add it)";
            return;
        }

        if (note is null)
        {
            MusicNoteEditTitle.Text = "Edit note";
            return;
        }

        int plays = musicView?.Notes.Count(other => other.Offset == note.Offset) ?? 1;
        MusicNoteEditTitle.Text = $"Edit note: track {note.Track + 1}, {Position(note.Start)}{(plays > 1 ? $" (played {plays} times)" : string.Empty)}";
        LoadNoteFields(note.Key, note.Velocity, note.Length, note.Start, note.Program);
    }

    private void LoadNoteFields(int key, int velocity, int length, int start, int program)
    {
        loadingNoteEdit = true;
        try
        {
            MusicEditKey.Value = key;
            MusicEditVelocity.Value = velocity;
            MusicEditLength.Value = length;
            MusicEditStart.Value = start;
            MusicEditProgram.Value = program;
            MusicEditKeyName.Text = PianoRollView.NoteName(Math.Clamp(key, 0, 127));
            MusicEditLengthBeats.Text = $"{length / (double)PianoRollView.TicksPerBeat:0.##} beats";
            MusicEditStartBar.Text = $"bar {Position(start)}";
        }
        finally
        {
            loadingNoteEdit = false;
        }
    }

    private void MusicEntryInfoRefresh()
    {
        if (SelectedMusic is not { } entry) return;
        int edits = pendingSequences.TryGetValue(entry.Info.Index, out PendingSequence? pending) ? pending.Operations.Count : 0;
        MusicTitle.Text = edits > 0 ? $"{entry.Info.Name} (unsaved note edits)" : entry.Info.Name;
        MusicInfo.Text = $"{entry.Category}; sequence file {entry.Info.FileId}, instrument bank {entry.Info.Bank}, volume {entry.Info.Volume}." + (edits > 0 ? $" {edits} note edit{(edits == 1 ? "" : "s")} not saved yet; Play uses them." : string.Empty);
    }

    private void MusicNoteList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (switchingNotes) return;
        NitroSequencer.NoteEvent? next = SelectedNote;
        if (next is null && musicView is not null && MusicNoteList.Items.Count > 0 && (shownNote is not null || creatingNote is not null)) return;

        if (creatingNote is { } creating)
        {
            if (next is null) return;
            if (creatingTouched)
            {
                (SequenceEditor.NoteOp? op, string? problem) = BuildCreateOp(creating.Reference);
                if (op is null)
                {
                    MusicStatus.Text = problem;
                    switchingNotes = true;
                    MusicNoteList.SelectedItem = null;
                    switchingNotes = false;
                    return;
                }

                creatingNote = null;
                _ = ApplyNoteEditAsync(op, creating.Reference, deleting: false, creating: true, thenSelect: next);
                return;
            }

            creatingNote = null;
        }
        else if (shownNote is { } previous && next is not null && !ReferenceEquals(previous, next))
        {
            (SequenceEditor.NoteOp? op, string? problem) = BuildEditOp(previous);
            if (problem is not null)
            {
                MusicStatus.Text = problem;
                RestoreNoteSelection(previous);
                return;
            }

            if (op is not null)
            {
                _ = ApplyNoteEditAsync(op, previous, deleting: false, creating: false, thenSelect: next);
                return;
            }
        }

        UpdateMusicEditState();
    }

    private void RestoreNoteSelection(NitroSequencer.NoteEvent note)
    {
        switchingNotes = true;
        try
        {
            MusicNoteList.SelectedItem = MusicNoteList.Items.OfType<NoteRow>().FirstOrDefault(row => ReferenceEquals(row.Note, note));
        }
        finally
        {
            switchingNotes = false;
        }
    }

    private void MusicEditKey_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingNoteEdit || MusicEditKeyName is null) return;
        MusicEditKeyName.Text = PianoRollView.NoteName(Math.Clamp((int)(MusicEditKey.Value ?? 60), 0, 127));
        PreviewNoteEdit();
    }

    private void MusicEditVelocity_Changed(object? sender, NumericUpDownValueChangedEventArgs e) => PreviewNoteEdit();

    private void MusicEditProgram_Changed(object? sender, NumericUpDownValueChangedEventArgs e) => PreviewNoteEdit();

    private void MusicEditStart_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingNoteEdit || MusicEditStartBar is null) return;
        MusicEditStartBar.Text = $"bar {Position((int)(MusicEditStart.Value ?? 0))}";
        PreviewNoteEdit();
    }

    private void PreviewNoteEdit()
    {
        if (loadingNoteEdit || MusicEditKey is null || MusicEditVelocity is null || MusicEditLength is null || MusicEditStart is null) return;
        int key = Math.Clamp((int)(MusicEditKey.Value ?? 60), 0, 127), velocity = (int)(MusicEditVelocity.Value ?? 100), length = Math.Max((int)(MusicEditLength.Value ?? 48), 1), start = Math.Max((int)(MusicEditStart.Value ?? 0), 0);
        if (creatingNote is { } creating)
        {
            creatingTouched = true;
            MusicRoll.SetGhost(creating.Track, start, key, velocity, length);
        }
        else if (SelectedNote is { } note)
        {
            MusicRoll.SetPreview(note.Offset, key, velocity, length, start - note.Start);
        }
    }

    private void MusicEditLength_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingNoteEdit || MusicEditLengthBeats is null) return;
        MusicEditLengthBeats.Text = $"{(int)(MusicEditLength.Value ?? 48) / (double)PianoRollView.TicksPerBeat:0.##} beats";
        PreviewNoteEdit();
    }

    private void MusicRoll_NoteClicked(NitroSequencer.NoteEvent note)
    {
        int row = MusicTracks.Items.OfType<TrackRow>().ToList().FindIndex(track => track.Track == note.Track);
        if (row >= 0 && MusicTracks.SelectedItem is TrackRow current && current.Track != note.Track) MusicTracks.SelectedIndex = row + 1;
        SelectMusicNote(note.Offset, note.Start);
    }

    private void SelectMusicNote(int offset, int start)
    {
        NoteRow? row = MusicNoteList.Items.OfType<NoteRow>().FirstOrDefault(item => item.Note.Offset == offset && item.Note.Start == start)
            ?? MusicNoteList.Items.OfType<NoteRow>().FirstOrDefault(item => item.Note.Offset == offset);
        MusicNoteList.SelectedItem = row;
        if (row is not null) MusicNoteList.ScrollIntoView(row);
    }

    private bool ProgramExists(int program)
    {
        var rows = MusicInstruments.Items.OfType<InstrumentRow>().ToList();
        return rows.Count == 0 || rows.Any(row => row.Instrument.Program == program);
    }

    /// <returns>The edit the fields describe for <paramref name="note"/>; no operation and no problem when nothing differs.</returns>
    private (SequenceEditor.NoteOp? Op, string? Problem) BuildEditOp(NitroSequencer.NoteEvent note)
    {
        int key = (int)(MusicEditKey.Value ?? note.Key), velocity = (int)(MusicEditVelocity.Value ?? note.Velocity), length = (int)(MusicEditLength.Value ?? note.Length);
        int start = (int)(MusicEditStart.Value ?? note.Start), program = (int)(MusicEditProgram.Value ?? note.Program);
        int stored = key - note.Transpose;
        if (stored is < 0 or > 127)
        {
            return (null, $"This track is transposed by {note.Transpose}, so its keys can only go from {PianoRollView.NoteName(Math.Max(note.Transpose, 0))} to {PianoRollView.NoteName(Math.Min(127 + note.Transpose, 127))}.");
        }

        if (program != note.Program && !ProgramExists(program)) return (null, $"This sequence's bank has no instrument {program}; see the list of instruments below.");
        if (stored == note.Key - note.Transpose && velocity == note.Velocity && length == note.Length && start == note.Start && program == note.Program) return (null, null);
        return (new SequenceEditor.NoteOp(note.Offset, stored, velocity, length, Waits: note.Waits, Shift: start - note.Start, Program: program != note.Program ? program : null, CurrentProgram: note.Program), null);
    }

    private (SequenceEditor.NoteOp? Op, string? Problem) BuildCreateOp(NitroSequencer.NoteEvent reference)
    {
        int key = (int)(MusicEditKey.Value ?? 60), velocity = (int)(MusicEditVelocity.Value ?? 100), length = (int)(MusicEditLength.Value ?? 48);
        int start = (int)(MusicEditStart.Value ?? 0), program = (int)(MusicEditProgram.Value ?? reference.Program);
        int stored = key - reference.Transpose;
        if (stored is < 0 or > 127) return (null, $"This track is transposed by {reference.Transpose}, so its keys can only go from {PianoRollView.NoteName(Math.Max(reference.Transpose, 0))} to {PianoRollView.NoteName(Math.Min(127 + reference.Transpose, 127))}.");
        if (program != reference.Program && !ProgramExists(program)) return (null, $"This sequence's bank has no instrument {program}; see the list of instruments below.");
        return (new SequenceEditor.NoteOp(reference.Offset, stored, velocity, length, Waits: reference.Waits, Shift: start - reference.Start, Program: program != reference.Program ? program : null, CurrentProgram: reference.Program, Create: true), null);
    }

    private void MusicNewNote_Click(object? sender, RoutedEventArgs e)
    {
        if (musicView is null) return;
        if (MusicTracks.SelectedItem is not TrackRow track)
        {
            MusicStatus.Text = "Pick the track for the new note in the track list first (not All tracks).";
            return;
        }

        var onTrack = musicView.Notes.Where(note => note.Track == track.Track).OrderBy(note => note.Start).ToList();
        int start = (int)Math.Round(MusicRoll.PlayheadTick / 12) * 12;
        NitroSequencer.NoteEvent reference = onTrack.LastOrDefault(note => note.Start <= start) ?? onTrack.First();
        creatingNote = (track.Track, reference);
        creatingTouched = false;
        switchingNotes = true;
        MusicNoteList.SelectedItem = null;
        switchingNotes = false;
        UpdateMusicEditState();
        LoadNoteFields(reference.Key, 100, reference.Waits ? Math.Min(reference.Length, 48) : 48, start, reference.Program);
        PreviewNoteEdit();
        creatingTouched = false;
        MusicStatus.Text = "Set the key, start, length and instrument of the new note and press Apply.";
    }

    private async void MusicEditApply_Click(object? sender, RoutedEventArgs e)
    {
        if (creatingNote is { } creating)
        {
            (SequenceEditor.NoteOp? created, string? createProblem) = BuildCreateOp(creating.Reference);
            if (created is null)
            {
                MusicStatus.Text = createProblem;
                return;
            }

            creatingNote = null;
            await ApplyNoteEditAsync(created, creating.Reference, deleting: false, creating: true);
            return;
        }

        if (SelectedNote is not { } note) return;
        (SequenceEditor.NoteOp? op, string? problem) = BuildEditOp(note);
        if (problem is not null)
        {
            MusicStatus.Text = problem;
            return;
        }

        if (op is null)
        {
            MusicStatus.Text = "Nothing changed.";
            return;
        }

        await ApplyNoteEditAsync(op, note, deleting: false, creating: false);
    }

    private async void MusicEditDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedNote is not { } note) return;
        await ApplyNoteEditAsync(new SequenceEditor.NoteOp(note.Offset, 0, 0, 1, Delete: true, Waits: note.Waits), note, deleting: true, creating: false);
    }

    private static List<(int, int, int, int, int, int)> Signature(IEnumerable<NitroSequencer.NoteEvent> notes) =>
        notes.Select(note => (note.Track, note.Key, note.Velocity, note.Start, note.Length, note.Program)).Order().ToList();

    private async Task ApplyNoteEditAsync(SequenceEditor.NoteOp operation, NitroSequencer.NoteEvent note, bool deleting, bool creating, NitroSequencer.NoteEvent? thenSelect = null)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null || SelectedMusic is not { } entry || musicView is null) return;
        if (!pendingSequences.TryGetValue(entry.Info.Index, out PendingSequence? pending))
        {
            try
            {
                pending = new PendingSequence(MusicService.ReadSequence(currentProject, metadata, entry.Info.Index));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                LogUiError("Sequence could not be read", ex);
                MusicStatus.Text = $"The sequence could not be read: {ex.Message}";
                return;
            }
        }

        var options = new NitroSequencer.Options((double)(MusicSeconds.Value ?? 120), (int)(MusicLoops.Value ?? 0), (int)(MusicEntrySize.Value ?? 4));
        NitroSequencer.SequenceView current = musicView;
        int expected = current.Notes.Count - (deleting ? current.Notes.Count(other => other.Offset == note.Offset) : 0);
        bool lengthChangeOnWaitingTrack = !deleting && !creating && operation.Waits && operation.Length != note.Length;
        SequenceEditor.Result result;
        NitroSequencer.SequenceView editedView;
        try
        {
            result = SequenceEditor.ApplyDetailed(pending.File, operation);
            NitroSequencer.SequenceView check = await Task.Run(() => MusicService.Notes(currentProject, metadata, entry.Info.Index, options, result.File));
            if (creating)
            {
                if (check.Notes.Count <= current.Notes.Count) throw new InvalidDataException("the new note does not play");
                if (!Signature(current.Notes).SequenceEqual(Signature(check.Notes.Where(other => other.Offset != result.NoteOffset)))) throw new InvalidDataException("the new note would disturb other notes");
            }
            else if (deleting || lengthChangeOnWaitingTrack)
            {
                if (check.Notes.Count != expected) throw new InvalidDataException($"the edit would change the number of notes from {expected} to {check.Notes.Count}");
            }
            else
            {
                if (check.Notes.Count != current.Notes.Count) throw new InvalidDataException($"the edit would change the number of notes from {current.Notes.Count} to {check.Notes.Count}");
                if (!Signature(current.Notes.Where(other => other.Offset != note.Offset)).SequenceEqual(Signature(check.Notes.Where(other => other.Offset != result.NoteOffset)))) throw new InvalidDataException("the edit would disturb other notes");
            }

            editedView = check;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        {
            LogUiError("Note could not be edited", ex);
            MusicStatus.Text = $"The note could not be changed: {ex.Message}";
            if (creating) creatingNote = (note.Track, note);
            if (thenSelect is not null) RestoreNoteSelection(note);
            return;
        }

        pending.File = result.File;
        pending.Operations.Add(operation);
        pendingSequences[entry.Info.Index] = pending;
        lastMusic = null;
        creatingNote = null;
        if (thenSelect is not null) reselectNote = (result.Map(thenSelect.Offset), thenSelect.Start);
        else if (!deleting) reselectNote = (result.NoteOffset, Math.Max(note.Start + operation.Shift, 0));
        else reselectNote = null;
        MusicStatus.Text = deleting ? "Deleted the note (not saved yet)." : creating ? "Added the note (not saved yet)." : "Changed the note (not saved yet).";
        StopMusicPlayhead();
        ShowMusicNotes(editedView);
    }

    private async void MusicEditSave_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedMusic is { } entry) await SaveSequenceEditsAsync(entry.Info.Index);
    }

    private async Task SaveSequenceEditsAsync(int sequenceIndex)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null || !pendingSequences.TryGetValue(sequenceIndex, out PendingSequence? pending)) return;
        try
        {
            MusicService.EditNotes(currentProject, metadata, sequenceIndex, pending.Operations);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            LogUiError("Sequence could not be saved", ex);
            MusicStatus.Text = $"The sequence could not be saved: {ex.Message}";
            return;
        }

        pendingSequences.Remove(sequenceIndex);
        lastMusic = null;
        MusicStatus.Text = $"Saved the note edits to the sound archive. Use Save ROM to build them into the ROM.";
        if (SelectedMusic is { } entry && entry.Info.Index == sequenceIndex) await LoadMusicNotesAsync(entry);
    }

    private async void MusicEditDiscard_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedMusic is not { } entry || !pendingSequences.Remove(entry.Info.Index)) return;
        lastMusic = null;
        MusicStatus.Text = "Discarded the unsaved note edits.";
        await LoadMusicNotesAsync(entry);
    }

    private void CollectUnsavedMusicChanges(List<UnsavedChange> changes)
    {
        foreach ((int index, PendingSequence pending) in pendingSequences.OrderBy(pair => pair.Key).ToList())
        {
            string name = musicEntries.FirstOrDefault(entry => entry.Info.Index == index)?.Info.Name ?? $"Sequence {index}";
            IReadOnlyList<string> details = [$"{name}: {pending.Operations.Count} note edit{(pending.Operations.Count == 1 ? "" : "s")}"];
            changes.Add(new UnsavedChange("Music Editor", "Music notes", details, () => _ = SaveSequenceEditsAsync(index), OpenDocument: false));
        }
    }
}
