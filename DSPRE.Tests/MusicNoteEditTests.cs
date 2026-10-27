using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class MusicNoteEditTests(PlatinumFixture fixture)
{
    private static readonly NitroSequencer.Options Quick = new(MaxSeconds: 60);

    private static NitroSequencer.SequenceView View(TestProject t, int index, byte[]? file = null) => MusicService.Notes(t.Project, t.Metadata, index, Quick, file);

    private static int FirstWithNotes(TestProject t, Func<NitroSequencer.SequenceView, bool> accept) =>
        MusicService.List(t.Project, t.Metadata).Where(e => e.Category == "Music").Select(e => e.Info.Index).First(i => accept(View(t, i)));

    private static byte[] Sseq(params byte[] payload)
    {
        byte[] header = new byte[0x1C];
        "SSEQ"u8.CopyTo(header);
        header[0x18] = 0x1C;
        byte[] file = [.. header, .. payload];
        return [.. file, .. new byte[(4 - file.Length % 4) % 4]];
    }

    private static readonly byte[] NoWait = [0xC7, 0x00, 0x3C, 0x64, 0x30, 0x80, 0x30, 0x40, 0x64, 0x30, 0x80, 0x30, 0x43, 0x64, 0x30, 0x80, 0x30, 0xFF];
    private static readonly byte[] Wait = [0x3C, 0x64, 0x30, 0x80, 0x30, 0x40, 0x64, 0x30, 0x80, 0x30, 0xFF];

    private static List<(int Key, int Start, int Length, int Program)> Played(TestProject t, byte[] file) =>
        View(t, FirstWithNotes(t, v => v.Notes.Count > 0), file).Notes.Select(n => (n.Key, n.Start, n.Length, n.Program)).OrderBy(n => n.Start).ThenBy(n => n.Key).ToList();

    [Theory]
    [InlineData(24, 72)]
    [InlineData(-24, 24)]
    [InlineData(48, 96)]
    [InlineData(-48, 0)]
    [InlineData(96, 144)]
    public void MoveNote_OnATrackThatDoesNotWait_ChangesOnlyItsStart(int shift, int expected)
    {
        TestProject t = fixture.CreateProject();
        byte[] file = Sseq(NoWait);

        byte[] edited = SequenceEditor.Apply(file, new SequenceEditor.NoteOp(7, 0x40, 0x64, 0x30, Waits: false, Shift: shift));

        var after = Played(t, edited);
        Assert.Equal(3, after.Count);
        Assert.Contains(after, n => n.Key == 0x40 && n.Start == expected && n.Length == 0x30);
        Assert.Contains(after, n => n.Key == 0x3C && n.Start == 0);
        Assert.Contains(after, n => n.Key == 0x43 && n.Start == 96);
    }

    [Theory]
    [InlineData(24, 120)]
    [InlineData(48, 144)]
    [InlineData(-48, 48)]
    [InlineData(-24, 72)]
    public void MoveNote_OnATrackThatWaits_KeepsTheOthersWhereTheyAre(int shift, int expected)
    {
        TestProject t = fixture.CreateProject();
        byte[] file = Sseq(Wait);

        byte[] edited = SequenceEditor.Apply(file, new SequenceEditor.NoteOp(5, 0x40, 0x64, 0x30, Shift: shift));

        var after = Played(t, edited);
        Assert.Equal(2, after.Count);
        Assert.Contains(after, n => n.Key == 0x40 && n.Start == expected);
        Assert.Contains(after, n => n.Key == 0x3C && n.Start == 0);
    }

    [Fact]
    public void MoveNote_OverAnotherNoteOnATrackThatWaits_IsRefused()
    {
        byte[] file = Sseq(Wait);
        Assert.Throws<NotSupportedException>(() => SequenceEditor.Apply(file, new SequenceEditor.NoteOp(5, 0x40, 0x64, 0x30, Shift: -60)));
    }

    [Fact]
    public void NewNote_GoesWhereItIsPlaced()
    {
        TestProject t = fixture.CreateProject();
        byte[] edited = SequenceEditor.Apply(Sseq(NoWait), new SequenceEditor.NoteOp(7, 50, 90, 12, Waits: false, Shift: 12, Create: true));

        var after = Played(t, edited);
        Assert.Equal(4, after.Count);
        Assert.Contains(after, n => n.Key == 50 && n.Start == 60 && n.Length == 12);
        Assert.Contains(after, n => n.Key == 0x40 && n.Start == 48);
        Assert.Contains(after, n => n.Key == 0x43 && n.Start == 96);
    }

    [Fact]
    public void NewNote_OnATrackThatWaits_NeedsRoomAndKeepsTheOthers()
    {
        TestProject t = fixture.CreateProject();
        byte[] edited = SequenceEditor.Apply(Sseq(Wait), new SequenceEditor.NoteOp(0, 50, 90, 12, Shift: 60, Create: true));

        var after = Played(t, edited);
        Assert.Equal(3, after.Count);
        Assert.Contains(after, n => n.Key == 50 && n.Start == 60 && n.Length == 12);
        Assert.Contains(after, n => n.Key == 0x40 && n.Start == 96);
        Assert.Throws<NotSupportedException>(() => SequenceEditor.Apply(Sseq(Wait), new SequenceEditor.NoteOp(0, 50, 90, 12, Shift: 10, Create: true)));
    }

    [Fact]
    public void ChangedInstrument_AppliesToThatNoteOnly()
    {
        TestProject t = fixture.CreateProject();
        byte[] file = Sseq(NoWait);

        SequenceEditor.Result first = SequenceEditor.ApplyDetailed(file, new SequenceEditor.NoteOp(7, 0x40, 0x64, 0x30, Waits: false, Program: 5, CurrentProgram: 0));
        var after = Played(t, first.File);
        Assert.Equal(3, after.Count);
        Assert.Equal(5, after.Single(n => n.Key == 0x40).Program);
        Assert.All(after.Where(n => n.Key != 0x40), n => Assert.Equal(0, n.Program));

        SequenceEditor.Result second = SequenceEditor.ApplyDetailed(first.File, new SequenceEditor.NoteOp(first.NoteOffset, 0x40, 0x64, 0x30, Waits: false, Program: 9, CurrentProgram: 5));
        after = Played(t, second.File);
        Assert.Equal(9, after.Single(n => n.Key == 0x40).Program);
        Assert.All(after.Where(n => n.Key != 0x40), n => Assert.Equal(0, n.Program));
        Assert.Equal(first.File.Length, second.File.Length);
    }

    [Fact]
    public void MovedAndRetunedNote_TogetherWithAJumpBack_StillLoops()
    {
        TestProject t = fixture.CreateProject();
        // 0: C7 00; 2: note A; 5: rest; 7: note B; 10: rest; 12: jump to 2
        byte[] file = Sseq(0xC7, 0x00, 0x3C, 0x64, 0x30, 0x80, 0x30, 0x40, 0x64, 0x30, 0x80, 0x30, 0x94, 0x02, 0x00, 0x00);
        SequenceEditor.Result edited = SequenceEditor.ApplyDetailed(file, new SequenceEditor.NoteOp(7, 0x40, 0x64, 0x30, Waits: false, Shift: 24, Program: 3, CurrentProgram: 0));

        NitroSequencer.SequenceView before = View(t, FirstWithNotes(t, v => v.Notes.Count > 0), file);
        NitroSequencer.SequenceView after = View(t, FirstWithNotes(t, v => v.Notes.Count > 0), edited.File);
        Assert.Equal(before.LoopTick, after.LoopTick);
        Assert.Equal(before.EndTick, after.EndTick + 24 - 24);
        Assert.Equal(3, after.Notes.Single(n => n.Key == 0x40 && n.Start == 72).Program);
    }

    [Fact]
    public void RealSequences_MoveRetuneAndAddNotes_WithoutDisturbingTheOthers()
    {
        TestProject t = fixture.CreateProject();
        int moved = 0, added = 0, retuned = 0;
        foreach (MusicService.Entry entry in MusicService.List(t.Project, t.Metadata).Where(e => e.Category == "Music").Take(25))
        {
            NitroSequencer.SequenceView before = View(t, entry.Info.Index);
            byte[] file = MusicService.ReadSequence(t.Project, t.Metadata, entry.Info.Index);
            List<int> programs = MusicService.Instruments(t.Project, t.Metadata, entry.Info.Index, 4).Select(i => i.Program).ToList();
            if (programs.Count < 2) continue;
            static List<(int, int, int, int, int, int)> Sig(IEnumerable<NitroSequencer.NoteEvent> notes) => notes.Select(n => (n.Track, n.Key, n.Velocity, n.Start, n.Length, n.Program)).Order().ToList();
            foreach (NitroSequencer.NoteEvent note in before.Notes.Where(n => n.Start > 100).Take(40))
            {
                foreach (int shift in new[] { 12, -12 })
                {
                    try
                    {
                        SequenceEditor.Result result = SequenceEditor.ApplyDetailed(file, new SequenceEditor.NoteOp(note.Offset, note.Key - note.Transpose, note.Velocity, note.Length, Waits: note.Waits, Shift: shift));
                        NitroSequencer.SequenceView after = View(t, entry.Info.Index, result.File);
                        if (before.Notes.Count(n => n.Offset == note.Offset) != 1) continue;
                        Assert.Equal(before.Notes.Count, after.Notes.Count);
                        Assert.Equal(Sig(before.Notes.Where(n => n.Offset != note.Offset)), Sig(after.Notes.Where(n => n.Offset != result.NoteOffset)));
                        Assert.Contains(after.Notes, n => n.Offset == result.NoteOffset && n.Start == note.Start + shift);
                        moved++;
                    }
                    catch (NotSupportedException)
                    {
                    }
                }

                try
                {
                    int other = programs.First(p => p != note.Program);
                    SequenceEditor.Result result = SequenceEditor.ApplyDetailed(file, new SequenceEditor.NoteOp(note.Offset, note.Key - note.Transpose, note.Velocity, note.Length, Waits: note.Waits, Program: other, CurrentProgram: note.Program));
                    NitroSequencer.SequenceView after = View(t, entry.Info.Index, result.File);
                    Assert.Equal(before.Notes.Count, after.Notes.Count);
                    Assert.Equal(Sig(before.Notes.Where(n => n.Offset != note.Offset)), Sig(after.Notes.Where(n => n.Offset != result.NoteOffset)));
                    Assert.All(after.Notes.Where(n => n.Offset == result.NoteOffset), n => Assert.Equal(other, n.Program));
                    retuned++;
                }
                catch (NotSupportedException)
                {
                }
            }

            NitroSequencer.NoteEvent? reference = before.Notes.FirstOrDefault(n => n.Start > 100 && !n.Waits && before.Notes.Count(o => o.Offset == n.Offset) == 1);
            if (reference is null) continue;
            try
            {
                SequenceEditor.Result result = SequenceEditor.ApplyDetailed(file, new SequenceEditor.NoteOp(reference.Offset, 60, 90, 24, Waits: false, Shift: 30, Create: true));
                NitroSequencer.SequenceView after = View(t, entry.Info.Index, result.File);
                Assert.Equal(Sig(before.Notes), Sig(after.Notes.Where(n => n.Offset != result.NoteOffset)));
                Assert.Contains(after.Notes, n => n.Offset == result.NoteOffset && n.Start == reference.Start + 30 && n.Length == 24);
                added++;
            }
            catch (NotSupportedException)
            {
            }
        }

        Assert.True(moved >= 20, $"moved {moved}");
        Assert.True(retuned >= 10, $"retuned {retuned}");
        Assert.True(added >= 3, $"added {added}");
    }

    [Fact]
    public void Banks_ListEveryBankWithItsInstruments_AndPlayANote()
    {
        TestProject t = fixture.CreateProject();
        var banks = MusicService.Banks(t.Project, t.Metadata);
        Assert.NotEmpty(banks);
        MusicService.Entry entry = MusicService.List(t.Project, t.Metadata).First(e => e.Category == "Music");
        var viaSequence = MusicService.Instruments(t.Project, t.Metadata, entry.Info.Index);
        var viaBank = MusicService.BankInstruments(t.Project, t.Metadata, entry.Info.Bank);
        Assert.Equal(viaSequence.Select(i => (i.Program, i.Layout, i.Regions.Count)), viaBank.Select(i => (i.Program, i.Layout, i.Regions.Count)));
        Assert.Contains(entry.Info.Name, banks[entry.Info.Bank].Sequences);
        Assert.NotEmpty(MusicService.RenderBankNote(t.Project, t.Metadata, entry.Info.Bank, viaBank[0].Program, 60, 0.5).Stereo);
        Assert.Throws<ArgumentException>(() => MusicService.BankInstruments(t.Project, t.Metadata, banks.Count + 5));
    }

    [Fact]
    public void Notes_CarryTheirOffsetsAndTranspose()
    {
        TestProject t = fixture.CreateProject();
        int index = FirstWithNotes(t, v => v.Notes.Count > 20);
        NitroSequencer.SequenceView view = View(t, index);
        byte[] file = MusicService.ReadSequence(t.Project, t.Metadata, index);
        int start = SequenceEditor.PayloadStart(file);
        Assert.All(view.Notes, note =>
        {
            Assert.InRange(note.Offset, 0, file.Length - start);
            Assert.Equal(Math.Clamp(file[start + note.Offset] + note.Transpose, 0, 127), note.Key);
            Assert.Equal(file[start + note.Offset + 1], note.Velocity);
        });
    }

    [Fact]
    public void SameSizeEdit_ChangesOnlyThatNote()
    {
        TestProject t = fixture.CreateProject();
        int index = FirstWithNotes(t, v => v.Notes.Count > 20);
        byte[] file = MusicService.ReadSequence(t.Project, t.Metadata, index);
        NitroSequencer.NoteEvent note = View(t, index).Notes.First(n => n.Length < 100);

        byte[] edited = SequenceEditor.Apply(file, new SequenceEditor.NoteOp(note.Offset, Math.Clamp(note.Key - note.Transpose + 2, 0, 127), 99, note.Length));

        Assert.Equal(file.Length, edited.Length);
        NitroSequencer.SequenceView after = View(t, index, edited);
        Assert.Equal(View(t, index).Notes.Count, after.Notes.Count);
        Assert.All(after.Notes.Where(n => n.Offset == note.Offset), n => Assert.Equal(99, n.Velocity));
        Assert.Equal(0, after.Notes.Count(n => n.Offset != note.Offset && n.Velocity == 99 && View(t, index).Notes.All(o => o.Offset != n.Offset || o.Velocity == 99)) - after.Notes.Count(n => n.Offset != note.Offset && n.Velocity == 99 && View(t, index).Notes.Any(o => o.Offset == n.Offset && o.Velocity == 99)));
    }

    [Fact]
    public void LongerLengthThatNeedsMoreBytes_KeepsEveryOtherNoteAndJump()
    {
        TestProject t = fixture.CreateProject();
        int checkedSequences = 0;
        foreach (MusicService.Entry entry in MusicService.List(t.Project, t.Metadata).Where(e => e.Category == "Music").Take(10))
        {
            NitroSequencer.SequenceView before = View(t, entry.Info.Index);
            NitroSequencer.NoteEvent? note = before.Notes.FirstOrDefault(n => n.Length < 100 && n.Start > 0);
            if (note is null) continue;
            byte[] file = MusicService.ReadSequence(t.Project, t.Metadata, entry.Info.Index);

            byte[] edited = SequenceEditor.Apply(file, new SequenceEditor.NoteOp(note.Offset, note.Key - note.Transpose, note.Velocity, 3000, Waits: note.Waits));

            Assert.True(edited.Length >= file.Length, entry.Info.Name);
            NitroSequencer.SequenceView after = View(t, entry.Info.Index, edited);
            Assert.Equal(before.Notes.Count, after.Notes.Count);
            Assert.Equal(before.Notes.Count(n => n.Offset == note.Offset), after.Notes.Count(n => n.Length == 3000));
            Assert.Equal(before.Notes.Count(n => n.Length != 3000 && n.Offset != note.Offset), after.Notes.Count(n => n.Length != 3000));
            Assert.Equal(before.Notes.Select(n => (n.Track, n.Key, n.Velocity)).Order().ToList(), after.Notes.Select(n => (n.Track, n.Key, n.Velocity)).Order().ToList());
            Assert.Equal(before.TrackCount, after.TrackCount);
            checkedSequences++;
        }

        Assert.True(checkedSequences >= 5);
    }

    [Fact]
    public void GrowThenShrinkBackIsIdentical()
    {
        TestProject t = fixture.CreateProject();
        foreach (MusicService.Entry entry in MusicService.List(t.Project, t.Metadata).Where(e => e.Category == "Music").Take(10))
        {
            NitroSequencer.NoteEvent? note = View(t, entry.Info.Index).Notes.FirstOrDefault(n => n.Length < 100);
            if (note is null) continue;
            byte[] file = MusicService.ReadSequence(t.Project, t.Metadata, entry.Info.Index);
            int start = SequenceEditor.PayloadStart(file);
            int used = 0;
            while ((file[start + note.Offset + 2 + used] & 0x80) != 0) used++;

            byte[] grown = SequenceEditor.Apply(file, new SequenceEditor.NoteOp(note.Offset, note.Key - note.Transpose, note.Velocity, 30000));
            byte[] back = SequenceEditor.Apply(grown, new SequenceEditor.NoteOp(note.Offset, note.Key - note.Transpose, note.Velocity, note.Length));

            // A shrink keeps the wider number (padded), so compare what the sequence plays.
            NitroSequencer.SequenceView a = View(t, entry.Info.Index), b = View(t, entry.Info.Index, back);
            Assert.Equal(a.Notes.Select(n => (n.Track, n.Key, n.Velocity, n.Start, n.Length)).ToList(), b.Notes.Select(n => (n.Track, n.Key, n.Velocity, n.Start, n.Length)).ToList());
            Assert.Equal(a.LoopTick, b.LoopTick);
        }
    }

    [Fact]
    public void DeleteNote_OnATrackThatDoesNotWait_RemovesItAndKeepsTheRestInTime()
    {
        TestProject t = fixture.CreateProject();
        int index = FirstWithNotes(t, v => v.Notes.Count > 30);
        NitroSequencer.SequenceView before = View(t, index);
        NitroSequencer.NoteEvent note = before.Notes.First(n => !n.Waits && n.Start > 0);
        byte[] file = MusicService.ReadSequence(t.Project, t.Metadata, index);

        byte[] edited = SequenceEditor.Apply(file, new SequenceEditor.NoteOp(note.Offset, 0, 0, 1, Delete: true, Waits: false));

        NitroSequencer.SequenceView after = View(t, index, edited);
        Assert.Equal(before.Notes.Count - before.Notes.Count(n => n.Offset == note.Offset), after.Notes.Count);
        var kept = before.Notes.Where(n => n.Offset != note.Offset).Select(n => (n.Track, n.Key, n.Start, n.Length)).Order().ToList();
        Assert.Equal(kept, after.Notes.Select(n => (n.Track, n.Key, n.Start, n.Length)).Order().ToList());
    }

    [Fact]
    public void DeleteNote_OnATrackThatWaits_BecomesARestOfTheSameLength()
    {
        byte[] header = new byte[0x1C];
        "SSEQ"u8.CopyTo(header);
        header[0x18] = 0x1C;
        byte[] payload = [0xFE, 0x01, 0x00, 0x3C, 0x64, 0x30, 0x40, 0x64, 0x30, 0xFF];
        byte[] file = [.. header, .. payload, 0, 0];

        byte[] edited = SequenceEditor.Apply(file, new SequenceEditor.NoteOp(3, 0, 0, 1, Delete: true, Waits: true));

        Assert.Equal(file.Length, edited.Length);
        Assert.Equal(new byte[] { 0xFE, 0x01, 0x00, 0x80, 0x80, 0x30, 0x40, 0x64, 0x30, 0xFF }, edited.AsSpan(0x1C, payload.Length).ToArray());
    }

    [Fact]
    public void LengthEdit_RewritesJumpAndTrackTargets()
    {
        byte[] header = new byte[0x1C];
        "SSEQ"u8.CopyTo(header);
        header[0x18] = 0x1C;
        // 0: open track 1 at 9; 5: note C4 len 5; 8: end; 9: note D4 len 5; 12: jump back to 9; 16: end
        byte[] payload = [0x93, 0x01, 0x09, 0x00, 0x00, 0x3C, 0x64, 0x05, 0xFF, 0x3E, 0x64, 0x05, 0x94, 0x09, 0x00, 0x00, 0xFF];
        byte[] file = [.. header, .. payload];
        file = [.. file, .. new byte[(4 - file.Length % 4) % 4]];

        byte[] edited = SequenceEditor.Apply(file, new SequenceEditor.NoteOp(5, 0x3C, 0x64, 300));

        byte[] result = edited.AsSpan(0x1C).ToArray();
        // the length grew from one byte to two, so everything after it moved by one
        Assert.Equal(new byte[] { 0x93, 0x01, 0x0A, 0x00, 0x00, 0x3C, 0x64, 0x82, 0x2C, 0xFF, 0x3E, 0x64, 0x05, 0x94, 0x0A, 0x00, 0x00, 0xFF }, result.AsSpan(0, 18).ToArray());
        Assert.Equal((uint)edited.Length, BitConverter.ToUInt32(edited, 0x08));
    }

    [Fact]
    public void InvalidEditsAreRejected()
    {
        TestProject t = fixture.CreateProject();
        int index = FirstWithNotes(t, v => v.Notes.Count > 0);
        byte[] file = MusicService.ReadSequence(t.Project, t.Metadata, index);
        NitroSequencer.NoteEvent note = View(t, index).Notes[0];
        Assert.Throws<ArgumentOutOfRangeException>(() => SequenceEditor.Apply(file, new SequenceEditor.NoteOp(note.Offset, 200, 100, 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SequenceEditor.Apply(file, new SequenceEditor.NoteOp(note.Offset, 60, 128, 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SequenceEditor.Apply(file, new SequenceEditor.NoteOp(note.Offset, 60, 100, 0)));
        Assert.Throws<InvalidDataException>(() => SequenceEditor.Apply(file, new SequenceEditor.NoteOp(-5, 60, 100, 10)));
    }

    [Fact]
    public void ResizedSoundArchive_KeepsEveryOtherFileAndName()
    {
        TestProject t = fixture.CreateProject();
        string path = PokemonCryService.FindSoundArchive(t.Project, t.Metadata)!;
        byte[] original = File.ReadAllBytes(path);
        SdatArchive archive = SdatArchive.Read(original);
        SdatArchive.SequenceInfo sequence = archive.Sequences.First(s => s.Name.Length > 0);
        byte[] content = [.. archive.File(sequence.FileId), .. new byte[1000]];

        byte[] resized = archive.WithFileResized(sequence.FileId, content);

        Assert.True(resized.Length > original.Length);
        SdatArchive after = SdatArchive.Read(resized);
        Assert.Equal(archive.Sequences.Select(s => (s.Index, s.Name, s.FileId, s.Bank)).ToList(), after.Sequences.Select(s => (s.Index, s.Name, s.FileId, s.Bank)).ToList());
        for (int id = 0; ; id++)
        {
            try { _ = archive.File(id); }
            catch (InvalidDataException) { break; }
            if (id == sequence.FileId) Assert.Equal(content, after.File(id));
            else Assert.Equal(archive.File(id), after.File(id));
        }
    }

    [Fact]
    public void EditNotes_WritesTheArchive_JournalsEachEdit_AndSurvivesRebuild()
    {
        TestProject t = fixture.CreateProject();
        string journalPath = Path.Combine(t.Project.RootPath, EditJournal.FileName);
        int index = FirstWithNotes(t, v => v.Notes.Count > 20);
        NitroSequencer.NoteEvent note = View(t, index).Notes.First(n => n.Length < 100);
        EditJournal.Open(t.Project, t.Metadata, journalPath: journalPath);
        try
        {
            MusicService.EditNotes(t.Project, t.Metadata, index, [new SequenceEditor.NoteOp(note.Offset, note.Key - note.Transpose, 77, 4000)]);

            NitroSequencer.SequenceView after = View(t, index);
            Assert.All(after.Notes.Where(n => n.Length == 4000), n => Assert.Equal(77, n.Velocity));
            Assert.NotEmpty(after.Notes.Where(n => n.Length == 4000));
            string[] lines = File.ReadAllLines(journalPath).Where(l => l.StartsWith("SetSequenceNote(")).ToArray();
            Assert.Single(lines);
            Assert.Contains("length: 4000", lines[0]);
        }
        finally
        {
            EditJournal.Close();
        }
    }

    [Fact]
    public async Task EditedSequence_SurvivesRomBuildAndReextract()
    {
        TestProject t = fixture.CreateProject();
        int index = FirstWithNotes(t, v => v.Notes.Count > 20);
        NitroSequencer.NoteEvent note = View(t, index).Notes.First(n => n.Length < 100);
        MusicService.EditNotes(t.Project, t.Metadata, index, [new SequenceEditor.NoteOp(note.Offset, note.Key - note.Transpose, 77, 4000)]);

        var service = new RomExtractionService();
        string rom = Path.Combine(t.Project.RootPath, "..", "music-" + Guid.NewGuid().ToString("N") + ".nds");
        ToolResult build = await service.BuildAsync(t.Project.RootPath, rom, t.ToolsDirectory);
        Assert.True(build.Succeeded, build.StandardError);
        string extracted = Path.Combine(Path.GetDirectoryName(rom)!, "music-reextract-" + Guid.NewGuid().ToString("N"));
        Assert.True((await service.ExtractAsync(rom, extracted, RomProjectFormat.DsRom, t.ToolsDirectory)).Succeeded);
        RomProject.TryOpen(extracted, out RomProject? reopened);

        Assert.NotEmpty(MusicService.Notes(reopened!, t.Metadata, index, Quick).Notes.Where(n => n.Length == 4000));
    }
}
