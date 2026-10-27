namespace DSPRE.Core;

public sealed record HeaderOption(ushort Id, string Label)
{
    public override string ToString() => $"[{Id:D3}] {Label}";
}

public static class HeaderDisplayCatalog
{
    public static IReadOnlyList<HeaderOption> GetWeather(MapHeaderFormat format) => format switch
    {
        MapHeaderFormat.HeartGoldSoulSilver =>
        [new(0, "Normal"), new(1, "Rain"), new(5, "Snow"), new(8, "Diamond Dust"), new(9, "Fog"), new(11, "Darkness"), new(12, "Darkness after Flash"), new(13, "Low Light")],
        MapHeaderFormat.Platinum =>
        [new(0, "Normal"), new(2, "Rain"), new(3, "Heavy rain"), new(4, "Thunderstorm"), new(5, "Snowfall"), new(6, "Diamond dust"), new(7, "Blizzard"), new(10, "Sand storm"), new(11, "Hail"), new(14, "Fog"), new(15, "Deep fog"), new(18, "Light fog"), new(19, "Heavy fog"), new(23, "Eterna forest weather"), new(26, "Dark fog")],
        _ =>
        [new(0, "Normal"), new(2, "Rain"), new(3, "Heavy rain"), new(4, "Thunderstorm"), new(5, "Snowfall"), new(6, "Diamond dust"), new(7, "Blizzard"), new(10, "Sand storm"), new(11, "Hail"), new(14, "Fog"), new(15, "Deep fog")]
    };

    public static IReadOnlyList<HeaderOption> GetCameraAngles(MapHeaderFormat format) => format == MapHeaderFormat.HeartGoldSoulSilver
        ? [new(0, "3D Top View"), new(1, "3D Front High"), new(2, "3D Lower"), new(3, "3D Front"), new(4, "2D Top View"), new(5, "Normal 3D"), new(7, "High 3D"), new(10, "3D High Wide"), new(11, "3D Front Wide"), new(15, "2D Higher"), new(16, "3D Top View")]
        : [new(0, "3D Normal"), new(1, "3D Top Higher"), new(2, "3D Front Low - Wide FOV"), new(3, "3D Front"), new(4, "2D Ortho"), new(5, "3D Normal - Wide FOV"), new(6, "3D Bird View"), new(8, "3D Bird View Far"), new(9, "3D Front - Wide FOV"), new(10, "3D Top - Narrow"), new(12, "3D Top"), new(14, "3D Top - Wide FOV"), new(15, "3D Front Low")];

    public static IReadOnlyList<HeaderOption> GetMusic(MapHeaderFormat format)
    {
        var options = new List<HeaderOption>
        {
            new(1000, "Mystery Zone"), new(1001, "Silence"), new(1002, "Silence (Dungeon)"),
            new(1004, "Twinleaf Town"), new(1005, "Sandgem Town"), new(1006, "Floaroma Town"),
            new(1007, "Solaceon Town"), new(1010, "Jubilife City"), new(1011, "Canalave City"),
            new(1012, "Oreburgh City"), new(1013, "Eterna City"), new(1014, "Hearthome City"),
            new(1015, "Pastoria City"), new(1016, "Veilstone City"), new(1017, "Sunyshore City"),
            new(1018, "Snowpoint City"), new(1019, "Pokemon League"), new(1021, "Route 201"),
            new(1022, "Route 203"), new(1023, "Route 205"), new(1024, "Route 206"), new(1025, "Route 209")
        };

        if (format == MapHeaderFormat.HeartGoldSoulSilver)
        {
            options.AddRange([new(1100, "New Bark Town"), new(1101, "Violet City"), new(1102, "Ecruteak City"), new(1103, "Goldenrod City"), new(1104, "Olivine City"), new(1105, "Cianwood City"), new(1106, "Mahogany Town"), new(1107, "Blackthorn City"), new(1108, "Pallet Town"), new(1109, "Viridian City"), new(1110, "Pewter City"), new(1111, "Cerulean City")]);
        }

        return options;
    }

    public static IReadOnlyList<HeaderOption> GetAreaSettings(MapHeaderFormat format) => format switch
    {
        MapHeaderFormat.HeartGoldSoulSilver => [new(0, "Mystery Zone"), new(1, "City/Town"), new(2, "Route"), new(3, "Cave"), new(4, "Interior (Hide Name)"), new(5, "Pokemon Center"), new(6, "Underground")],
        _ => [new(0, "Show"), new(1, "Show"), new(2, "Show"), new(3, "Show"), new(4, "Don't show"), new(128, "Don't show"), new(129, "Show"), new(130, "Show"), new(131, "Show"), new(132, "Don't show"), new(134, "Show")]
    };
}
