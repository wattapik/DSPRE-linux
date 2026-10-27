namespace DSPRE.Core;

public enum RomLanguage
{
    English,
    Spanish,
    Italian,
    French,
    German,
    Japanese
}

public sealed record RomGameMetadata(
    string GameCode,
    MapHeaderFormat HeaderFormat,
    RomLanguage Language,
    long HeaderTableOffset)
{
    public static bool TryResolve(string? gameCode, out RomGameMetadata? metadata)
    {
        metadata = null;
        if (string.IsNullOrWhiteSpace(gameCode))
        {
            return false;
        }

        string code = gameCode.Trim().ToUpperInvariant();
        if (code.Length != 4)
        {
            return false;
        }

        MapHeaderFormat? format = code[..3] switch
        {
            "ADA" or "APA" => MapHeaderFormat.DiamondPearl,
            "CPU" => MapHeaderFormat.Platinum,
            "IPK" or "IPG" => MapHeaderFormat.HeartGoldSoulSilver,
            _ => null
        };

        if (format is null)
        {
            return false;
        }

        RomLanguage language = code[3] switch
        {
            'E' => RomLanguage.English,
            'S' => RomLanguage.Spanish,
            'I' => RomLanguage.Italian,
            'F' => RomLanguage.French,
            'D' => RomLanguage.German,
            _ => RomLanguage.Japanese
        };

        long offset = ResolveHeaderTableOffset(code, format.Value, language);
        metadata = new RomGameMetadata(code, format.Value, language, offset);
        return true;
    }

    private static long ResolveHeaderTableOffset(string code, MapHeaderFormat format, RomLanguage language)
    {
        if (format == MapHeaderFormat.DiamondPearl)
        {
            return language switch
            {
                RomLanguage.English => 0xEEDBC,
                RomLanguage.Spanish => 0xEEE08,
                RomLanguage.Italian => 0xEED70,
                RomLanguage.French => 0xEEDFC,
                RomLanguage.German => 0xEEDCC,
                RomLanguage.Japanese => code.StartsWith("ADA") ? 0xF0D68 : 0xF0D6C,
                _ => throw new ArgumentOutOfRangeException(nameof(language))
            };
        }

        if (format == MapHeaderFormat.Platinum)
        {
            return language switch
            {
                RomLanguage.English => 0xE601C,
                RomLanguage.Spanish => 0xE60B0,
                RomLanguage.Italian => 0xE6038,
                RomLanguage.French => 0xE60A4,
                RomLanguage.German => 0xE6074,
                RomLanguage.Japanese => 0xE56F0,
                _ => throw new ArgumentOutOfRangeException(nameof(language))
            };
        }

        return language switch
        {
            RomLanguage.English => 0xF6BE0,
            RomLanguage.Spanish => code.StartsWith("IPK") ? 0xF6BC8 : 0xF6BD0,
            RomLanguage.Italian => 0xF6B58,
            RomLanguage.French => 0xF6BC4,
            RomLanguage.German => 0xF6B94,
            RomLanguage.Japanese => 0xF6390,
            _ => throw new ArgumentOutOfRangeException(nameof(language))
        };
    }
}
