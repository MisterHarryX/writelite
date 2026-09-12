using System.Diagnostics;
using WriteLite.Language.Russian;

namespace WriteLite.Services.Spelling;

public sealed class SeedSpellDictionary : ISpellDictionary
{
    private readonly HashSet<string> _russianWords;
    private readonly Dictionary<string, int> _russianFrequency;

    private SeedSpellDictionary(
        HashSet<string> russianWords,
        Dictionary<string, int> russianFrequency,
        TimeSpan loadTime)
    {
        _russianWords = russianWords;
        _russianFrequency = russianFrequency;
        Stats = new SpellDictionaryStats(
            russianWords.Count,
            0,
            loadTime,
            "Built-in Russian WriteLite seed dictionary",
            "Project-local seed list authored for WriteLite; no third-party dictionary data.",
            IsFullDictionaryLoaded: false);
    }

    public bool IsFullDictionaryLoaded => false;
    public string Language => RussianLanguageProfile.IsoCode;

    public DictionaryMetadata Metadata => new(
        Stats.Source,
        "ru-seed-1",
        Stats.License,
        string.Empty,
        Stats.RussianWordCount,
        null,
        SchemaVersion: 1,
        Language: RussianLanguageProfile.IsoCode);

    public SpellDictionaryStats Stats { get; }

    public static SeedSpellDictionary Load()
    {
        var stopwatch = Stopwatch.StartNew();
        var russian = new[]
        {
            ("привет", 10000), ("здравствуйте", 9990), ("человек", 9980), ("приложение", 9970),
            ("программа", 9960), ("компьютер", 9950), ("сообщение", 9940), ("предложение", 9930),
            ("словарь", 9920), ("исправление", 9910), ("проверка", 9900), ("работает", 9890),
            ("красивый", 9880), ("магазин", 9870), ("друг", 9860), ("пользователь", 9850),
            ("интерфейс", 9840), ("ошибка", 9830), ("текст", 9820), ("слово", 9810),
            ("русский", 9800), ("английский", 9790), ("смешанный", 9780), ("локальный", 9770),
            ("интеллект", 9760), ("поддержка", 9750), ("настройки", 9740), ("диагностика", 9730),
            ("в", 9720), ("общем", 9710), ("компания", 9700), ("тест", 9000), ("текста", 8500),
            ("поле", 8400), ("ошибки", 8290), ("ошибок", 8280), ("ошибками", 8270),
            ("замечание", 8200), ("исправления", 8090), ("проверяемое", 8080), ("проверяемого", 8070),
            ("словарное", 8060), ("словарного", 8050), ("лишние", 7990), ("пробелы", 7980),
            ("появляется", 7940), ("пример", 7600), ("когда", 7500), ("который", 7400),
            ("заказ", 7300), ("вчера", 7200), ("я", 7100), ("хочу", 7000), ("твои", 6990),
            ("нужно", 6980)
        };

        var frequencies = russian.ToDictionary(item => item.Item1, item => item.Item2, StringComparer.Ordinal);
        stopwatch.Stop();
        return new SeedSpellDictionary(
            frequencies.Keys.ToHashSet(StringComparer.Ordinal),
            frequencies,
            stopwatch.Elapsed);
    }

    public bool Check(string word) => Contains(word, SpellingLanguage.Russian);
    public IReadOnlyList<string> Suggest(string word) => [];
    public bool Contains(string word, SpellingLanguage language) => _russianWords.Contains(word);
    public int Frequency(string word, SpellingLanguage language) => _russianFrequency.GetValueOrDefault(word);
    public IReadOnlySet<string> Words(SpellingLanguage language) => _russianWords;
}
