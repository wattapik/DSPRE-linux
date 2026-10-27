namespace DSPRE.Core;

/// <summary>Names and paint colors for map "collision" (walkability/surface) and "type"
/// (movement/behavior) permission byte values, ported 1:1 from legacy's
/// DS_Map/Resources/PokeDatabase.cs (MapCollisionPainters/MapCollisionTypePainters) and
/// DS_Map/Editors/MapEditor.cs (PrepareCollisionPainterGraphics/PrepareTypePainterGraphics).</summary>
public static class MapPermissionPalette
{
    public const byte DefaultTransparency = 128;

    public static readonly IReadOnlyDictionary<byte, string> CollisionNames = new Dictionary<byte, string>
    {
        [0x00] = "[00] Walkable",
        [0x01] = "[01] Snow",
        [0x02] = "[02] Leaves",
        [0x04] = "[04] Grass",
        [0x06] = "[06] Stairs & Ice",
        [0x07] = "[07] Metal",
        [0x0A] = "[0A] Cavern floor",
        [0x0D] = "[0D] Wood",
        [0x80] = "[80] Blocked",
    };

    public static readonly IReadOnlyDictionary<byte, string> TypeNames = new Dictionary<byte, string>
    {
        [0x00] = "[00] None",
        [0x02] = "[02] Tall Grass (Wild)",
        [0x03] = "[03] Very Tall Grass (Wild)",
        [0x06] = "[06] Headbutt - No Encounter",
        [0x07] = "[07] Headbutt - Common Encounter",
        [0x08] = "[08] Cave Floor",
        [0x09] = "[09] Headbutt - Rare Encounter",
        [0x0A] = "[0A] Headbutt - Special Encounter",
        [0x0B] = "[0B] Old Château floor",
        [0x0C] = "[0C] Ground Mountain",
        [0x10] = "[10] River Water (Wild)",
        [0x11] = "[11] Whirlpool (HGSS)",
        [0x13] = "[13] Waterfall",
        [0x15] = "[15] Sea Water (Wild)",
        [0x16] = "[16] Puddle",
        [0x17] = "[17] Shallow Walkable water",
        [0x1D] = "[1D] Puddle (No Splashing)",
        [0x20] = "[20] Ice",
        [0x21] = "[21] Sand",
        [0x22] = "[22] Cave Underwater",
        [0x24] = "[24] Safari Zone Border",
        [0x2C] = "[2C] Magma",
        [0x2D] = "[2D] Reflection",
        [0x30] = "[30] Block Right",
        [0x31] = "[31] Block Left",
        [0x32] = "[32] Block Up",
        [0x33] = "[33] Block Down",
        [0x34] = "[34] Block Right & Up",
        [0x35] = "[35] Block Left & Up",
        [0x36] = "[36] Block Right & Down",
        [0x37] = "[37] Block Left & Down",
        [0x38] = "[38] Jump Right",
        [0x39] = "[39] Jump Left",
        [0x3A] = "[3A] Jump Up (Broken in HGSS)",
        [0x3B] = "[3B] Jump Down",
        [0x3C] = "[3C] Ladder front",
        [0x3D] = "[3D] Ladder back",
        [0x3E] = "[3E] Ladder down",
        [0x3F] = "[3F] Jump Corner DownLeft",
        [0x40] = "[40] Slide Right",
        [0x41] = "[41] Slide Left",
        [0x42] = "[42] Slide Up",
        [0x43] = "[43] Slide Down",
        [0x49] = "[49] Block Down & Up",
        [0x4A] = "[4A] Block Left & Right",
        [0x4B] = "[4B] Vert Rock Climb",
        [0x4C] = "[4C] Horiz Rock Climb",
        [0x4D] = "[4D] Stop Sliding",
        [0x5A] = "[5A] Jump Up Two Tiles",
        [0x5B] = "[5B] Jump Down Two Tiles",
        [0x5C] = "[5C] Jump Left Two Tiles",
        [0x5D] = "[5D] Jump Right Two Tiles",
        [0x5E] = "[5E] Stairs Warp (Right)",
        [0x5F] = "[5F] Stairs Warp (Left)",
        [0x62] = "[62] Warp Entrance (Right)",
        [0x63] = "[63] Warp Entrance (Left)",
        [0x64] = "[64] Warp Entrance (Up)",
        [0x65] = "[65] Warp Entrance (Down)",
        [0x67] = "[67] Warp Panel",
        [0x69] = "[69] Door Warp",
        [0x6A] = "[6A] Escalator Warp (Down-Right)",
        [0x6B] = "[6B] Escalator Warp (Up-Right)",
        [0x6C] = "[6C] Warp Right",
        [0x6D] = "[6D] Warp Left",
        [0x6E] = "[6E] Warp Up",
        [0x6F] = "[6F] Warp Down",
        [0x70] = "[70] Bridge Start",
        [0x71] = "[71] Bridge Middle",
        [0x72] = "[72] Bridge Over Cave",
        [0x73] = "[73] Bridge Over Water",
        [0x74] = "[74] Bridge Over Sand",
        [0x75] = "[75] Bridge Over Snow",
        [0x76] = "[76] Vertical bike bridge",
        [0x77] = "[77] Vertical bridge (ground enc)",
        [0x78] = "[78] Vertical bike bridge over water",
        [0x79] = "[79] Vertical bike bridge over sand",
        [0x7A] = "[7A] Horizontal bike bridge",
        [0x7B] = "[7B] Horizontal bike bridge over ground encounters",
        [0x7C] = "[7C] Horizontal bike bridge over water",
        [0x7D] = "[7D] Horizontal bike bridge over sand",
        [0x80] = "[80] Table",
        [0x83] = "[83] Storage PC",
        [0x85] = "[85] Open TownMap",
        [0x86] = "[86] TV",
        [0xA0] = "[A0] Farm Land",
        [0xA1] = "[A1] Deep Snow",
        [0xA2] = "[A2] Very Deep Snow",
        [0xA3] = "[A3] Ultra Deep Snow",
        [0xA4] = "[A4] Mud",
        [0xA5] = "[A5] Deep Mud",
        [0xA6] = "[A6] Mud Grass 1",
        [0xA7] = "[A7] Mud Grass 2",
        [0xA8] = "[A8] Snow",
        [0xA9] = "[A9] Snow with Shadows",
        [0xD5] = "[D5] Bridge (Horizontal)",
        [0xD7] = "[D7] Bike Jump Left",
        [0xD8] = "[D8] Bike Jump Right",
        [0xD9] = "[D9] Bike Slope Top",
        [0xDA] = "[DA] Bike Slope Bottom",
        [0xDB] = "[DB] Bike parking",
        [0xE0] = "[E0] Small Bookshelf 1",
        [0xE1] = "[E1] Large Bookshelf 1",
        [0xE2] = "[E2] Large Bookshelf 2",
        [0xE3] = "[E3] Pot",
        [0xE4] = "[E4] Trash Can",
        [0xE5] = "[E5] Store Shelf 1",
        [0xE6] = "[E6] Blueprint",
        [0xEA] = "[EA] Small Bookshelf 2",
        [0xEB] = "[EB] Store Shelf 2",
        [0xEC] = "[EC] Store Shelf 3",
        [0xFF] = "[FF] No Attribute",
    };

    /// <summary>ARGB color for a collision-grid cell, matching PrepareCollisionPainterGraphics.</summary>
    public static (byte A, byte R, byte G, byte B) GetCollisionColor(byte value, byte transparency = DefaultTransparency) => value switch
    {
        0x01 => (transparency, (byte)230, (byte)230, (byte)250), // Snow -> Lavender
        0x02 => (transparency, (byte)34, (byte)139, (byte)34),   // Leaves -> ForestGreen
        0x04 => (transparency, (byte)50, (byte)205, (byte)50),   // Grass -> LimeGreen
        0x06 => (transparency, (byte)176, (byte)224, (byte)230), // Stairs & Ice -> PowderBlue
        0x07 => (transparency, (byte)192, (byte)192, (byte)192), // Metal -> Silver
        0x0A => (transparency, (byte)105, (byte)105, (byte)105), // Cavern floor -> DimGray
        0x0D => (transparency, (byte)139, (byte)69, (byte)19),   // Wood -> SaddleBrown
        0x80 => (transparency, (byte)255, (byte)0, (byte)0),     // Blocked -> Red
        _ => ((byte)32, (byte)255, (byte)255, (byte)255),        // Walkable -> White @ fixed alpha
    };

    /// <summary>ARGB color for a type-grid cell, matching PrepareTypePainterGraphics.</summary>
    public static (byte A, byte R, byte G, byte B) GetTypeColor(byte value, byte transparency = DefaultTransparency) => value switch
    {
        0x00 => ((byte)32, (byte)255, (byte)255, (byte)255),          // White @ fixed alpha
        0x02 => (transparency, (byte)50, (byte)205, (byte)50),        // LimeGreen
        0x03 => (transparency, (byte)0, (byte)128, (byte)0),          // Green
        0x06 => ((byte)128, (byte)154, (byte)205, (byte)50),          // YellowGreen @ fixed alpha
        0x07 => ((byte)128, (byte)0, (byte)100, (byte)0),             // DarkGreen @ fixed alpha
        0x08 => ((byte)128, (byte)222, (byte)184, (byte)135),         // BurlyWood @ fixed alpha
        0x09 => ((byte)128, (byte)106, (byte)90, (byte)205),          // SlateBlue @ fixed alpha
        0x0A => ((byte)128, (byte)255, (byte)99, (byte)71),           // Tomato @ fixed alpha
        0x0C => (transparency, (byte)222, (byte)184, (byte)135),      // BurlyWood
        0x10 => (transparency, (byte)135, (byte)206, (byte)235),      // SkyBlue
        0x13 => (transparency, (byte)70, (byte)130, (byte)180),       // SteelBlue
        0x15 => (transparency, (byte)65, (byte)105, (byte)225),       // RoyalBlue
        0x16 => (transparency, (byte)119, (byte)136, (byte)153),      // LightSlateGray
        0x20 => (transparency, (byte)0, (byte)255, (byte)255),        // Cyan
        0x21 => (transparency, (byte)255, (byte)218, (byte)185),      // PeachPuff
        0x30 or 0x31 or 0x32 or 0x33 => (transparency, (byte)255, (byte)0, (byte)0), // Red
        0x3C or 0x3D or 0x3E => ((byte)0x7F, (byte)0x65, (byte)0x43, (byte)0x21),    // fixed ARGB 0x7F654321
        0x38 or 0x39 or 0x3A or 0x3B => (transparency, (byte)128, (byte)0, (byte)0), // Maroon
        0x40 or 0x41 or 0x42 or 0x43 => (transparency, (byte)255, (byte)215, (byte)0), // Gold
        0x4B or 0x4C => (transparency, (byte)160, (byte)82, (byte)45), // Sienna
        0x5E => (transparency, (byte)153, (byte)50, (byte)204),        // DarkOrchid
        0x5F or 0x62 or 0x63 or 0x64 or 0x65 or 0x69 => (transparency, (byte)153, (byte)50, (byte)204), // DarkOrchid
        0x6C or 0x6D or 0x6E or 0x6F => (transparency, (byte)153, (byte)50, (byte)204), // DarkOrchid
        0xA1 or 0xA2 or 0xA3 => (transparency, (byte)240, (byte)255, (byte)240), // Honeydew
        0xA4 => (transparency, (byte)205, (byte)133, (byte)63),        // Peru
        0xA6 => (transparency, (byte)46, (byte)139, (byte)87),         // SeaGreen
        _ => ((byte)32, (byte)255, (byte)255, (byte)255),              // default -> White @ fixed alpha
    };
}
