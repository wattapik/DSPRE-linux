using System.Buffers.Binary;

namespace DSPRE.Core;

/// <summary>Whether the ROM Toolbox's Dynamic Header Allocation patch is in the ARM9, which decides where
/// the game - and so the editor - reads map headers from: the dynamic-headers NARC when it is, the
/// ARM9's header table otherwise. Ported from legacy PatchToolboxDialog.CheckFilesDynamicHeadersPatchApplied
/// and ToolboxDB.DynamicHeadersPatchData.getDynamicHeadersInitOffset: the patch's routine starts with
/// "push {lr}" (0xB500) at a per-ROM offset. Vanilla Platinum also ships the NARC the patch reuses
/// (debug/cb_edit/d_test.narc, unused debug data), so the NARC's presence alone proves nothing.</summary>
public static class DynamicHeadersPatch
{
    private const ushort PushLr = 0xB500;

    public static bool IsApplied(RomProject project, RomGameMetadata metadata)
    {
        if (!File.Exists(project.Arm9Path)) return false;
        long offset = InitOffset(metadata.GameCode);
        using FileStream stream = File.OpenRead(project.Arm9Path);
        if (offset + 2 > stream.Length) return false;
        Span<byte> value = stackalloc byte[2];
        stream.Position = offset;
        stream.ReadExactly(value);
        return BinaryPrimitives.ReadUInt16LittleEndian(value) == PushLr;
    }

    private static uint InitOffset(string gameCode) => gameCode switch
    {
        "CPUE" => 0x3A024,
        "CPUS" or "CPUI" or "CPUF" or "CPUD" => 0x3A0C8,
        "CPUJ" => 0x39BE0,
        "IPKS" => 0x3B260,
        "IPKJ" or "IPGJ" => 0x3AE20,
        _ => 0x3B268
    };
}
