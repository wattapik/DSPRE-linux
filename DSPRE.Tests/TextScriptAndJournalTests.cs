using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class TextScriptAndJournalTests(PlatinumFixture fixture)
{
    [Fact]
    public void TextArchive_EditAddRemovePersist()
    {
        TestProject t = fixture.CreateProject();
        int id = TextArchiveService.GetArchiveIds(t.Project).First(i => TextArchiveService.Load(t.Project, i, t.Metadata).Messages.Count > 3);
        TextArchiveData archive = TextArchiveService.Load(t.Project, id, t.Metadata);
        int count = archive.Messages.Count;

        archive.Messages[1] = "Edited by a unit test.";
        archive.Messages.Add("A brand new line.");
        TextArchiveService.Save(t.Project, archive);

        TextArchiveData after = TextArchiveService.Load(t.Project, id, t.Metadata);
        Assert.Equal(count + 1, after.Messages.Count);
        Assert.Equal("Edited by a unit test.", after.Messages[1]);
        Assert.Equal("A brand new line.", after.Messages[^1]);
        Assert.Equal(archive.Key, after.Key);

        after.Messages.RemoveAt(after.Messages.Count - 1);
        TextArchiveService.Save(t.Project, after);
        Assert.Equal(count, TextArchiveService.Load(t.Project, id, t.Metadata).Messages.Count);
    }

    [Fact]
    public void ScriptFile_SaveWritesContent()
    {
        RomProject p = fixture.CreateProject().Project;
        Directory.CreateDirectory(p.ExpandedScriptsDirectory);
        const string content = "// edited by a unit test\nScript 1 {\n    End\n}\n";
        ScriptFileService.Save(p, 9999, content);
        Assert.Equal(content, File.ReadAllText(ScriptFileService.GetPath(p, 9999)));
    }

    [Fact]
    public void Journal_RecordsEditsInOrder()
    {
        TestProject t = fixture.CreateProject();
        string journalPath = Path.Combine(t.Project.RootPath, EditJournal.FileName);
        EditJournal.Open(t.Project, t.Metadata, journalPath: journalPath);
        try
        {
            PersonalDataRecord record = PersonalDataService.Load(t.Project, 387);
            record.BaseHP = 111;
            PersonalDataService.Save(t.Project, 387, record);
            MoveRecord move = MoveDataService.Load(t.Project, 33);
            move.Pp = 40;
            MoveDataService.Save(t.Project, 33, move);

            string[] lines = File.ReadAllLines(journalPath).Where(l => !l.StartsWith('#')).ToArray();
            Assert.StartsWith("BaseRom(", lines[0]);
            Assert.Contains(lines, l => l.Contains("BaseHP") && l.Contains("111"));
            Assert.Contains(lines, l => l.Contains("Pp") && l.Contains("40"));
            Assert.True(Array.FindIndex(lines, l => l.Contains("BaseHP")) < Array.FindIndex(lines, l => l.Contains("Pp")));
        }
        finally
        {
            EditJournal.Close();
        }
    }

    [Fact]
    public void Journal_UnchangedSaveWritesNothing()
    {
        TestProject t = fixture.CreateProject();
        string journalPath = Path.Combine(t.Project.RootPath, EditJournal.FileName);
        EditJournal.Open(t.Project, t.Metadata, journalPath: journalPath);
        try
        {
            string before = File.ReadAllText(journalPath);
            PersonalDataService.Save(t.Project, 387, PersonalDataService.Load(t.Project, 387));
            Assert.Equal(before, File.ReadAllText(journalPath));
        }
        finally
        {
            EditJournal.Close();
        }
    }

    [Fact]
    public void Journal_SuppressedEditsAreNotRecorded()
    {
        TestProject t = fixture.CreateProject();
        string journalPath = Path.Combine(t.Project.RootPath, EditJournal.FileName);
        EditJournal.Open(t.Project, t.Metadata, journalPath: journalPath);
        try
        {
            string before = File.ReadAllText(journalPath);
            using (EditJournal.Suppress())
            {
                PersonalDataRecord record = PersonalDataService.Load(t.Project, 387);
                record.BaseHP = 99;
                PersonalDataService.Save(t.Project, 387, record);
            }
            Assert.Equal(before, File.ReadAllText(journalPath));
        }
        finally
        {
            EditJournal.Close();
        }
    }

    [Fact]
    public async Task EditedProject_RebuildsIntoRomThatCarriesTheEdit()
    {
        TestProject t = fixture.CreateProject();
        PersonalDataRecord record = PersonalDataService.Load(t.Project, 387);
        record.BaseHP = 222;
        PersonalDataService.Save(t.Project, 387, record);

        var service = new RomExtractionService();
        service.PackPokemonData(t.Project, t.Metadata);
        string rom = Path.Combine(t.Project.RootPath, "..", "rebuilt-" + Guid.NewGuid().ToString("N") + ".nds");
        ToolResult build = await service.BuildAsync(t.Project.RootPath, rom, t.ToolsDirectory);
        Assert.True(build.Succeeded, build.StandardError);
        Assert.True(RomExtractionService.LooksLikeNintendoDsRom(rom));

        string extracted = Path.Combine(Path.GetDirectoryName(rom)!, "reextract-" + Guid.NewGuid().ToString("N"));
        ToolResult extract = await service.ExtractAsync(rom, extracted, RomProjectFormat.DsRom, t.ToolsDirectory);
        Assert.True(extract.Succeeded, extract.StandardError);
        RomProject.TryOpen(extracted, out RomProject? reopened);
        service.PreparePokemonData(reopened!, t.Metadata);
        Assert.Equal(222, PersonalDataService.Load(reopened!, 387).BaseHP);
    }
}
