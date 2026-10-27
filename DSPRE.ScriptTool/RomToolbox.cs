using System.Reflection;
using System.Resources;
using DSPRE;
using DSPRE.Resources;
using DSPRE.Resources.ROMToolboxDB;
using DSPRE.ROMFiles;
using static DSPRE.Resources.ROMToolboxDB.ToolboxDB;
using static DSPRE.RomInfo;

/// <summary>The legacy ROM Toolbox (DS_Map/PatchToolboxDialog.cs) without its dialogs, for the
/// cross-platform app. Status checks reuse the dialog's own public checks and patch data; each Apply
/// repeats the steps of the dialog's button handler, including its backups. The dialog's confirmation
/// boxes are the caller's job.
///
/// Output of "status", one line each: PATCH|id|applied/available/unavailable|reason, SYNTH|path of the
/// synthetic overlay file, DEFAULT|id|offset (hex)|length for the patches written at a chosen
/// synthetic-overlay offset.</summary>
static class RomToolbox
{
    private const uint ScrcmdBlockDefaultOffset = 0x200;
    private const uint ScrcmdOriginalCommandCount = 0x355;
    private const int ScrcmdOriginalTableLength = (int)(4 * ScrcmdOriginalCommandCount);
    private const uint ScrcmdCountOffsetInBlock = 0x04;
    private const uint ScrcmdTableMarkerOffsetInBlock = 0x08;
    private const uint ScrcmdTableOffsetInBlock = 0x0C;
    private const uint ScrcmdCountMarker = 0x4E554F43; // "COUN"
    private const uint ScrcmdTableMarker = 0x4C424154; // "TABL"
    private const string BackupSuffix = ".backup";

    public static void Prepare()
    {
        PatchToolboxDialog.ResetFlags();
        DSUtils.TryUnpackNarcs([DirNames.synthOverlay, DirNames.scripts, DirNames.eventFiles, DirNames.areaData]);
    }

    public static bool PrintStatus()
    {
        Prepare();
        bool arm9Supported = ARM9PatchData.arm9ExpansionCodeDB.ContainsKey($"branchString_{gameFamily}_{gameLanguage}");
        bool arm9Expanded = arm9Supported && Arm9ExpansionApplied();
        bool englishOrSpanish = gameLanguage is GameLanguages.English or GameLanguages.Spanish;

        Report("arm9Expansion", () => !arm9Supported ? Unavailable("Not supported for this game or language.")
            : arm9Expanded ? Applied() : Available());

        Report("bdhcam", () =>
        {
            if (!BDHCAMPatchData.SupportsCurrentRom() || gameFamily == GameFamilies.DP) return Unavailable("Not supported for this game or language.");
            if (!IsDsRomProject) return Unavailable("Convert the project to the ds-rom format first.");
            if (!arm9Expanded) return Unavailable("Apply the ARM9 Expansion first.");
            return PatchToolboxDialog.CheckFilesBDHCamPatchApplied() ? Applied() : Available();
        });

        Report("buildingRotation", () =>
        {
            if (!BuildingRotationPatchData.SupportsCurrentRom()) return Unavailable("Not supported for this game or language.");
            if (!IsDsRomProject) return Unavailable("Convert the project to the ds-rom format first.");
            if (!arm9Expanded) return Unavailable("Apply the ARM9 Expansion first.");
            return PatchToolboxDialog.CheckFilesBuildingRotationPatchApplied() ? Applied() : Available();
        });

        Report("sentenceCase", () => Available());

        Report("standardizeItems", () => PatchToolboxDialog.CheckScriptsStandardizedItemNumbers() ? Applied() : Available());

        Report("matrixExpansion", () => gameFamily != GameFamilies.HGSS || !englishOrSpanish ? Unavailable("Only HeartGold/SoulSilver (English or Spanish).")
            : PatchToolboxDialog.CheckFilesMatrixExpansionApplied() ? Applied() : Available());

        Report("repointScrcmd", () =>
        {
            if (gameFamily != GameFamilies.HGSS || !englishOrSpanish) return Unavailable("Only HeartGold/SoulSilver (English or Spanish).");
            if (!arm9Expanded) return Unavailable("Apply the ARM9 Expansion first.");
            return ScrcmdTableRepointed() ? Applied() : Available();
        });

        Report("dynamicHeaders", () =>
        {
            if (gameFamily == GameFamilies.DP) return Unavailable("Not supported for Diamond/Pearl.");
            return PatchToolboxDialog.CheckFilesDynamicHeadersPatchApplied() ? Applied() : Available();
        });

        Report("disableTextureAnimations", () => gameFamily != GameFamilies.HGSS ? Unavailable("Only HeartGold/SoulSilver use texture animations.")
            : AllTextureAnimationsDisabled() ? Applied() : Available());

        Report("expandTrainerNames", () => trainerNameLenOffset < 0 ? Unavailable("Not supported for this game or language.")
            : trainerNameMaxLen > TrainerFile.defaultNameLen ? Applied() : Available());

        Console.WriteLine($"SYNTH|{Filesystem.expArmPath}");
        if (BuildingRotationPatchData.SupportsCurrentRom())
        {
            var rotation = new BuildingRotationPatchData();
            Console.WriteLine($"DEFAULT|buildingRotation|{rotation.defaultPayloadOffset:X}|{rotation.payload.Length}");
        }

        if (gameFamily == GameFamilies.HGSS && englishOrSpanish)
        {
            Console.WriteLine($"DEFAULT|repointScrcmd|{ScrcmdBlockDefaultOffset:X}|{12 + ScrcmdOriginalTableLength}");
        }

        return true;
    }

    private static void Report(string id, Func<(string State, string Reason)> check)
    {
        (string state, string reason) result;
        try
        {
            result = check();
        }
        catch (Exception ex)
        {
            result = Unavailable($"Could not be checked: {ex.Message}");
        }

        Console.WriteLine($"PATCH|{id}|{result.state}|{result.reason.Replace('|', '/').Replace('\n', ' ')}");
    }

    private static (string, string) Applied() => ("applied", "");
    private static (string, string) Available() => ("available", "");
    private static (string, string) Unavailable(string reason) => ("unavailable", reason);

    public static bool Apply(string id, uint? offset)
    {
        Prepare();
        switch (id)
        {
            case "arm9Expansion": ApplyArm9Expansion(); break;
            case "bdhcam": ApplyBdhcam(); break;
            case "buildingRotation": ApplyBuildingRotation(offset); break;
            case "sentenceCase": ApplySentenceCase(); break;
            case "standardizeItems": ApplyStandardizeItems(); break;
            case "matrixExpansion": ApplyMatrixExpansion(); break;
            case "repointScrcmd": ApplyScrcmdRepoint(offset); break;
            case "dynamicHeaders": ApplyDynamicHeaders(); break;
            case "disableTextureAnimations": ApplyDisableTextureAnimations(); break;
            case "expandTrainerNames": ApplyExpandTrainerNames(); break;
            default: throw new ArgumentException($"Unknown patch '{id}'.");
        }

        return true;
    }

    // PatchToolboxDialog.CheckFilesArm9ExpansionApplied is private; this is the same comparison.
    private static bool Arm9ExpansionApplied()
    {
        var data = new ARM9PatchData();
        byte[] branch = DSUtils.HexStringToByteArray(data.branchString);
        byte[] init = DSUtils.HexStringToByteArray(data.initString);
        return ARM9.ReadBytes(data.branchOffset, data.branchString.Length / 3 + 1).SequenceEqual(branch)
            && ARM9.ReadBytes(data.initOffset, data.initString.Length / 3 + 1).SequenceEqual(init);
    }

    private static void RequireArm9Expansion()
    {
        if (!Arm9ExpansionApplied()) throw new InvalidOperationException("Apply the ARM9 Expansion first.");
    }

    private static void Backup(string path) => File.Copy(path, path + BackupSuffix, overwrite: true);

    private static void ApplyArm9Expansion()
    {
        if (Arm9ExpansionApplied()) throw new InvalidOperationException("The ARM9 Expansion is already applied.");
        var data = new ARM9PatchData();
        Backup(arm9Path);
        ARM9.WriteBytes(DSUtils.HexStringToByteArray(data.branchString), data.branchOffset);
        ARM9.WriteBytes(DSUtils.HexStringToByteArray(data.initString), data.initOffset);

        // As legacy: a synthetic overlay file already 88KB or larger may hold earlier work and is kept.
        string path = Filesystem.expArmPath;
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length >= 0x16000)
            {
                Console.WriteLine("NOTE|The synthetic overlay already exists and was left as it is.");
            }
            else
            {
                File.WriteAllBytes(path, new byte[0x16000]);
            }
        }
    }

    private static void ApplyBdhcam()
    {
        RequireArm9Expansion();
        if (PatchToolboxDialog.CheckFilesBDHCamPatchApplied()) throw new InvalidOperationException("Dynamic Cameras are already applied.");
        var data = new BDHCAMPatchData();
        string overlayPath = OverlayUtils.GetPath(data.overlayNumber);
        Backup(arm9Path);
        Backup(overlayPath);
        ARM9.WriteBytes(DSUtils.HexStringToByteArray(data.branchString), data.branchOffset);
        DSUtils.WriteToFile(overlayPath, DSUtils.HexStringToByteArray(data.overlayString1), data.overlayOffset1);
        DSUtils.WriteToFile(overlayPath, DSUtils.HexStringToByteArray(data.overlayString2), data.overlayOffset2);
        DSUtils.WriteToFile(Filesystem.expArmPath, data.subroutine, BDHCAMPatchData.BDHCamSubroutineOffset);
    }

    private static byte[] BuildThumbBl(uint sourceAddress, uint targetAddress)
    {
        int distance = unchecked((int)(targetAddress - (sourceAddress + 4)));
        ushort first = (ushort)(0xF000 | ((distance >> 12) & 0x07FF));
        ushort second = (ushort)(0xF800 | ((distance >> 1) & 0x07FF));
        return [(byte)(first & 0xFF), (byte)(first >> 8), (byte)(second & 0xFF), (byte)(second >> 8)];
    }

    private static void ApplyBuildingRotation(uint? offset)
    {
        RequireArm9Expansion();
        if (PatchToolboxDialog.CheckFilesBuildingRotationPatchApplied()) throw new InvalidOperationException("Building Rotation is already applied.");
        var data = new BuildingRotationPatchData();
        uint payloadOffset = offset ?? data.defaultPayloadOffset;
        uint payloadAddress = synthOverlayLoadAddress + payloadOffset;
        byte[] payload = (byte[])data.payload.Clone();
        BuildThumbBl(payloadAddress + BuildingRotationPatchData.payloadInternalBranchOffset, data.rotationMatrixFunctionAddress)
            .CopyTo(payload, (int)BuildingRotationPatchData.payloadInternalBranchOffset);

        string overlayPath = OverlayUtils.GetPath(data.overlayNumber);
        Backup(overlayPath);
        DSUtils.WriteToFile(overlayPath, BuildThumbBl(data.hookRuntimeAddress, payloadAddress), data.hookOverlayOffset);
        DSUtils.WriteToFile(Filesystem.expArmPath, payload, payloadOffset);
    }

    private static void ApplySentenceCase()
    {
        foreach (int archiveId in pokemonNamesTextNumbers)
        {
            var archive = new TextArchive(archiveId);
            for (int i = 1; i < archive.messages.Count; i++)
            {
                if (!PokeDatabase.System.pokeNames.TryGetValue((ushort)i, out string? name)) continue;
                archive.messages[i] = archive.messages[i].Replace(name.ToUpperInvariant(), name);
            }

            archive.SaveToExpandedDir(archiveId, showSuccessMessage: false);
        }
    }

    private static void ApplyStandardizeItems()
    {
        if (PatchToolboxDialog.CheckScriptsStandardizedItemNumbers()) throw new InvalidOperationException("Item numbers are already standardized.");
        var itemScriptFile = new ScriptFile(itemScriptFileNumber);
        int[] vanillaItems = new int[itemScriptFile.allScripts.Count - 1];
        for (int i = 0; i < vanillaItems.Length; i++)
        {
            vanillaItems[i] = BitConverter.ToInt16(itemScriptFile.allScripts[i].commands[0].cmdParams[1], 0);
        }

        // Ground items point at the item scripts by number, so every event file follows the new order.
        (int itemScriptMin, int itemScriptMax) = (7000, 8000);
        int eventFileCount = Filesystem.GetEventFileCount();
        for (int i = 0; i < eventFileCount; i++)
        {
            var eventFile = new EventFile(i);
            bool dirty = false;
            foreach (Overworld overworld in eventFile.overworlds)
            {
                bool isItem = overworld.type == (ushort)Overworld.OwType.ITEM
                    || (overworld.scriptNumber >= itemScriptMin && overworld.scriptNumber <= itemScriptMax);
                if (!isItem) continue;
                int itemScriptId = overworld.scriptNumber - (itemScriptMin - 1);
                overworld.scriptNumber = (ushort)(itemScriptMin + vanillaItems[itemScriptId - 1]);
                dirty = true;
            }

            if (dirty) eventFile.SaveToFileDefaultDir(i, showSuccessMessage: false);
        }

        // Platinum's Distortion World gives the Griseous Orb from overlay 9, by item script number.
        if (gameFamily == GameFamilies.Plat)
        {
            string overlay9 = OverlayUtils.GetPath(9);
            const int griseousOrbOffset = 0x8E20 + 10;
            int itemScriptId;
            using (var reader = new DSUtils.EasyReader(overlay9, griseousOrbOffset))
            {
                itemScriptId = reader.ReadUInt16() - (itemScriptMin - 1);
            }

            using var writer = new DSUtils.EasyWriter(overlay9, griseousOrbOffset);
            writer.Write((ushort)(itemScriptMin + vanillaItems[itemScriptId - 1]));
        }

        int itemCount = new TextArchive(itemNamesTextNumber).messages.Count;
        var executeGive = new ScriptCommandContainer((uint)itemCount + 1, itemScriptFile.allScripts[itemScriptFile.allScripts.Count - 1]);
        itemScriptFile.allScripts.Clear();
        for (ushort i = 0; i < itemCount; i++)
        {
            List<ScriptCommand> commands =
            [
                new ScriptCommand("SetVar 0x8008 " + i),
                new ScriptCommand("SetVar 0x8009 0x1"),
                new ScriptCommand("Jump Function_#1")
            ];
            itemScriptFile.allScripts.Add(new ScriptCommandContainer((ushort)(i + 1), ScriptFile.ContainerTypes.Script, commandList: commands));
        }

        itemScriptFile.allScripts.Add(executeGive);
        itemScriptFile.allFunctions[0].usedScriptID = itemCount + 1;
        itemScriptFile.SaveToFileDefaultDir(itemScriptFileNumber, showSuccessMessage: false);
    }

    private static void ApplyMatrixExpansion()
    {
        if (PatchToolboxDialog.CheckFilesMatrixExpansionApplied()) throw new InvalidOperationException("Matrix 0 is already expanded.");
        int languageOffset = romID is "IPKE" or "IPGE" or "IPGS" ? 8 : 0;
        Backup(arm9Path);
        foreach (KeyValuePair<uint[], string> change in matrixExpansionDB)
        {
            foreach (uint address in change.Key)
            {
                ARM9.WriteBytes(DSUtils.HexStringToByteArray(change.Value), (uint)(address - ARM9.address + languageOffset));
            }
        }
    }

    private static void ApplyDynamicHeaders()
    {
        if (gameFamily == GameFamilies.DP) throw new InvalidOperationException("Dynamic headers are not supported for Diamond/Pearl.");
        if (PatchToolboxDialog.CheckFilesDynamicHeadersPatchApplied()) throw new InvalidOperationException("Dynamic headers are already applied.");
        var data = new DynamicHeadersPatchData();
        var headersDirectory = gameDirs[DirNames.dynamicHeaders];

        // Read before the ARM9 changes below move the table's references.
        int headerCount = GetHeaderCount();
        var headers = new List<byte[]>(headerCount);
        for (int i = 0; i < headerCount; i++) headers.Add(MapHeader.LoadFromARM9((ushort)i).ToByteArray());

        Backup(arm9Path);
        ARM9.WriteBytes(DSUtils.HexStringToByteArray(data.initString), data.initOffset);
        foreach (Tuple<uint, uint> reference in DynamicHeadersPatchData.dynamicHeadersPointersDB[gameFamily])
        {
            ARM9.WriteBytes(DSUtils.HexStringToByteArray(data.REFERENCE_STRING), (uint)(reference.Item1 + data.pointerDiff));
            uint pointer = BitConverter.ToUInt32(ARM9.ReadBytes((uint)(reference.Item2 + data.pointerDiff), 4), 0) - headerTableOffset - ARM9.address;
            ARM9.WriteBytes(BitConverter.GetBytes(pointer), (uint)(reference.Item2 + data.pointerDiff));
        }

        // HGSS outside Japan and Spain has an instruction between the "mov r1, #0x18" and "mul r1, r0"
        // the loop above neutralizes, at one spot, so legacy patches that spot separately.
        if (gameFamily == GameFamilies.HGSS && gameLanguage != GameLanguages.Japanese && gameLanguage != GameLanguages.Spanish)
        {
            ARM9.WriteBytes(DSUtils.HexStringToByteArray(data.specialCaseData1), (uint)(data.specialCaseOffset1 + data.pointerDiff));
            ARM9.WriteBytes(DSUtils.HexStringToByteArray(data.specialCaseData2), (uint)(data.specialCaseOffset2 + data.pointerDiff));
            ARM9.WriteBytes(DSUtils.HexStringToByteArray(data.specialCaseData3), (uint)(data.specialCaseOffset3 + data.pointerDiff));
        }

        if (Directory.Exists(headersDirectory.unpackedDir)) Directory.Delete(headersDirectory.unpackedDir, true);
        Directory.CreateDirectory(headersDirectory.unpackedDir);
        for (int i = 0; i < headerCount; i++)
        {
            File.WriteAllBytes(Path.Combine(headersDirectory.unpackedDir, i.ToString("D4")), headers[i]);
        }
    }

    private static bool AllTextureAnimationsDisabled()
    {
        string[] files = Directory.GetFiles(gameDirs[DirNames.areaData].unpackedDir);
        return files.Length > 0 && files.All(path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            return new AreaData(stream).dynamicTextureType == 0xFFFF;
        });
    }

    private static void ApplyDisableTextureAnimations()
    {
        foreach (string path in Directory.GetFiles(gameDirs[DirNames.areaData].unpackedDir))
        {
            AreaData areaData;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                areaData = new AreaData(stream);
            }

            areaData.dynamicTextureType = 0xFFFF;
            areaData.SaveToFile(path, showSuccessMessage: false);
        }
    }

    private static void ApplyExpandTrainerNames()
    {
        if (trainerNameLenOffset < 0) throw new InvalidOperationException("Not supported for this game or language.");
        if (trainerNameMaxLen > TrainerFile.defaultNameLen) throw new InvalidOperationException("Trainer names are already expanded.");
        using var writer = new ARM9.Writer(trainerNameLenOffset);
        writer.Write((byte)PatchToolboxDialog.expandedTrainerNameLength);
    }

    private static int ScrcmdDbInt(string key)
    {
        var database = new ResourceManager("DSPRE.Resources.ROMToolboxDB.CustomScrCmdDB", typeof(RomInfo).Assembly);
        string value = database.GetString($"{key}_{gameVersion}_{gameLanguage}") ?? throw new NotSupportedException("No ScrCommands data for this game.");
        return int.Parse(value);
    }

    private static bool ScrcmdTableRepointed()
    {
        try
        {
            uint table;
            using (var reader = new ARM9.Reader(ScrcmdDbInt("pointerOffset"))) table = reader.ReadUInt32();
            if (table < synthOverlayLoadAddress || !File.Exists(Filesystem.expArmPath)) return false;
            uint offset = table - synthOverlayLoadAddress;
            long length = new FileInfo(Filesystem.expArmPath).Length;
            if (offset < ScrcmdTableOffsetInBlock || offset + (long)ScrcmdOriginalTableLength > length) return false;

            uint block = offset - ScrcmdTableOffsetInBlock;
            using (var synth = new BinaryReader(File.OpenRead(Filesystem.expArmPath)))
            {
                synth.BaseStream.Position = block;
                if (synth.ReadUInt32() != ScrcmdCountMarker) return false;
                synth.BaseStream.Position = block + ScrcmdTableMarkerOffsetInBlock;
                if (synth.ReadUInt32() != ScrcmdTableMarker) return false;
            }

            using var countReader = new ARM9.Reader(ScrcmdDbInt("commandCountPointerOffset"));
            return countReader.ReadUInt32() == synthOverlayLoadAddress + block + ScrcmdCountOffsetInBlock;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FormatException)
        {
            return false;
        }
    }

    private static void ApplyScrcmdRepoint(uint? offset)
    {
        RequireArm9Expansion();
        if (ScrcmdTableRepointed()) throw new InvalidOperationException("The ScrCommands table is already moved.");
        uint block = offset ?? ScrcmdBlockDefaultOffset;
        byte[] originalTable = DSUtils.ReadFromFile(arm9Path, ScrcmdDbInt("originalTableOffset"), ScrcmdOriginalTableLength);
        uint commandCount;
        using (var reader = new ARM9.Reader(ScrcmdDbInt("commandCountOffset"))) commandCount = reader.ReadUInt32();

        Backup(arm9Path);
        using (var synth = new BinaryWriter(new FileStream(Filesystem.expArmPath, FileMode.Open)))
        {
            synth.BaseStream.Position = block;
            synth.Write(ScrcmdCountMarker);
            synth.Write(commandCount);
            synth.Write(ScrcmdTableMarker);
            synth.Write(originalTable);
        }

        using (var writer = new ARM9.Writer(ScrcmdDbInt("pointerOffset"))) writer.Write(synthOverlayLoadAddress + block + ScrcmdTableOffsetInBlock);
        using (var writer = new ARM9.Writer(ScrcmdDbInt("commandCountPointerOffset"))) writer.Write(synthOverlayLoadAddress + block + ScrcmdCountOffsetInBlock);
    }
}
