namespace WriteLite.Services;

/// <summary>
/// Russian numeric agreement for counts shown in the interface.
/// </summary>
/// <remarks>
/// Russian picks one of three forms from the last two digits, so a naive
/// <c>count is >= 2 and &lt;= 4</c> test is right for 2–4 and wrong for 22, 23, 24 —
/// which is how "23 слов" reached the editor status bar. A product that corrects the
/// user's grammar cannot get its own wrong.
/// </remarks>
public static class RussianPlural
{
    /// <summary>
    /// Picks the form matching <paramref name="count"/>.
    /// </summary>
    /// <param name="one">Form for 1, 21, 31 … ("слово").</param>
    /// <param name="few">Form for 2–4, 22–24 … ("слова").</param>
    /// <param name="many">Form for 0, 5–20, 25–30 … ("слов").</param>
    public static string Form(int count, string one, string few, string many)
    {
        var absolute = Math.Abs(count);
        var lastTwo = absolute % 100;

        // The teens are the exception: 11–14 always take the "many" form.
        if (lastTwo is >= 11 and <= 14)
        {
            return many;
        }

        return (absolute % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many
        };
    }

    /// <summary>"1 слово", "23 слова", "5 слов".</summary>
    public static string Words(int count) => $"{count} {Form(count, "слово", "слова", "слов")}";

    /// <summary>"1 правило", "23 правила", "5 правил".</summary>
    public static string Rules(int count) => $"{count} {Form(count, "правило", "правила", "правил")}";

    /// <summary>"1 замечание", "23 замечания", "5 замечаний".</summary>
    public static string Issues(int count) => $"{count} {Form(count, "замечание", "замечания", "замечаний")}";

    /// <summary>"1 словарь", "23 словаря", "5 словарей".</summary>
    public static string Dictionaries(int count) => $"{count} {Form(count, "словарь", "словаря", "словарей")}";
}
