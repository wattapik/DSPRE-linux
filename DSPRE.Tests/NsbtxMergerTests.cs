using System.Buffers.Binary;
using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class NsbtxMergerTests(PlatinumFixture fixture)
{
    private static IEnumerable<byte[]> Packs(TestProject t) =>
        Enumerable.Range(0, TexturePackService.Count(t.Project, TexturePackKind.Map)).Select(id => File.ReadAllBytes(TexturePackService.GetPath(t.Project, TexturePackKind.Map, id)));

    [Fact]
    public void ARebuiltPackHasTheSameTexturesAndPalettesAndEveryNameResolves()
    {
        TestProject t = fixture.CreateProject();
        int checkedPacks = 0;
        foreach (byte[] original in Packs(t))
        {
            NsbtxMerger.Pack pack;
            try { pack = NsbtxMerger.Parse(original); }
            catch (InvalidDataException) { continue; }

            byte[] rebuilt = NsbtxMerger.Build(pack);
            NsbtxMerger.Pack again = NsbtxMerger.Parse(rebuilt);
            Assert.Equal(pack.TextureNames, again.TextureNames);
            Assert.Equal(pack.PaletteNames, again.PaletteNames);
            Assert.Equal(NsbtxPaletteLayout.ReadTextureNames(original), NsbtxPaletteLayout.ReadTextureNames(rebuilt));
            Assert.Equal(NsbtxPaletteLayout.Read(original).Select(slot => slot.Name), NsbtxPaletteLayout.Read(rebuilt).Select(slot => slot.Name));
            checkedPacks++;
        }

        Assert.True(checkedPacks > 10);
    }

    [Fact]
    public void ADictionaryTreeResolvesEveryNameToItsOwnEntry()
    {
        foreach (string[] names in new[]
        {
            new[] { "a" },
            new[] { "allpeak", "asphalt", "blueglay", "blueglayp", "bridge", "brown", "conttree_b", "conttree_t", "criff", "criffp", "sea", "tree01", "tshadow_lm2_pl" },
            Enumerable.Range(0, 200).Select(i => $"tex{i * 7919 % 1000:D3}_lm{i}").ToArray()
        })
        {
            byte[][] nodes = NsbtxMerger.BuildTree(names.Select(name => { byte[] key = new byte[16]; System.Text.Encoding.ASCII.GetBytes(name).CopyTo(key, 0); return key; }).ToArray());
            for (int i = 0; i < names.Length; i++) Assert.Equal(i, NsbtxMerger.Search(nodes, names[i]));
        }
    }

    [Fact]
    public void AddingTexturesKeepsTheirBytesAndMakesThemFindableByName()
    {
        TestProject t = fixture.CreateProject();
        byte[][] packs = Packs(t).Where(pack => { try { NsbtxMerger.Parse(pack); return true; } catch (InvalidDataException) { return false; } }).Take(2).ToArray();
        NsbtxMerger.Pack source = NsbtxMerger.Parse(packs[1]);
        string texture = source.TextureNames.First(name => !NsbtxMerger.Parse(packs[0]).TextureNames.Contains(name));

        NsbtxMerger.Result result = NsbtxMerger.Add(packs[0], packs[1], [texture], []);

        Assert.Equal([texture], result.AddedTextures);
        NsbtxMerger.Pack merged = NsbtxMerger.Parse(result.Data);
        Assert.Equal(NsbtxMerger.Parse(packs[0]).TextureNames.Append(texture), merged.TextureNames);
        Assert.Contains(texture, NsbtxPaletteLayout.ReadTextureNames(result.Data));
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(result.Data.AsSpan(8)), (uint)result.Data.Length);
        Assert.Empty(NsbtxMerger.Add(result.Data, packs[1], [texture], []).AddedTextures);
    }
}
