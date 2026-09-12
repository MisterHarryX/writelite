using WriteLite.Language.Russian;

namespace WriteLite.AI.Local;

public static class LanguageDetector
{
    public static string Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "und";
        }

        var cyr = 0;
        foreach (var ch in text)
        {
            if (ch is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё')
            {
                cyr++;
            }
        }

        // Latin fragments are allowed as product names, code, paths and URLs,
        // but WriteLite does not treat Latin-only prose as a supported language.
        return cyr > 0 ? RussianLanguageProfile.IsoCode : "und";
    }
}
