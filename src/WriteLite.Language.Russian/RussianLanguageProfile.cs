namespace WriteLite.Language.Russian;

/// <summary>Russian language helpers used by spelling and punctuation layers.</summary>
public static class RussianLanguageProfile
{
    public const string IsoCode = "ru";
    public static bool IsCyrillicLetter(char c)
        => (c >= 'а' && c <= 'я') || (c >= 'А' && c <= 'Я') || c is 'ё' or 'Ё';

    public static string FoldYo(string value) => value.Replace('ё', 'е').Replace('Ё', 'Е');
}
