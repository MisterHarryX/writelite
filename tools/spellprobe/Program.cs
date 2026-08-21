using WriteLite.Language.Core;
using WriteLite.Language.Russian;
using WriteLite.Services.Spelling;

// Forensics for the wrong-replacement cluster (Phase 5 §16). For each token the benchmark
// got wrong, this prints every candidate the generator produced with its score, plus
// whether the expected answer was in the index at all — which separates a ranking failure
// from a candidate-generation failure.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var index = RussianFormIndex.Load();
if (index is null)
{
    Console.WriteLine("form index not deployed");
    return;
}

var generator = new RussianCandidateGenerator(index, new EnglishLayoutLexicon());

(string Typed, string Want)[] cases =
[
    ("хвостм", "хвостом"),
    ("учебнки", "учебники"),
    ("неделч", "неделю"),
    ("страницв", "страница"),
    ("времч", "времени"),
    ("доброжелательнй", "доброжелательный"),
    ("двер", "дверь"),
    ("зонтк", "зонтик"),
    ("затиели", "затеяли"),
    ("Гагарен", "Гагарин"),
    ("Тургеньев", "Тургенев"),
    ("Ярославли", "Ярославле"),
    ("Репена", "Репина"),
    ("Тарковскава", "Тарковского"),
    ("Шолохава", "Шолохова"),
    ("Айвазовскава", "Айвазовского"),
    ("Дискорте", "Дискорде"),
    ("Ниросеть", "Нейросеть"),
    ("профел", "профиль"),
    ("конфег", "конфиг"),
    ("продакшин", "продакшен"),
    ("мoре", "море"),
    ("сьел", "съел"),
];

foreach (var (typed, want) in cases)
{
    var wantLower = want.ToLowerInvariant();
    var inIndex = index.Contains(wantLower);
    var info = index.GetInfo(wantLower);
    var ranked = generator.Rank(typed, 8);
    var position = -1;
    for (var i = 0; i < ranked.Count; i++)
    {
        if (string.Equals(ranked[i].Word, wantLower, StringComparison.OrdinalIgnoreCase))
        {
            position = i;
        }
    }

    var verdict = !inIndex ? "NOT-IN-INDEX"
        : position < 0 ? "NOT-GENERATED"
        : position == 0 ? "ok"
        : $"RANKED-{position}";

    Console.WriteLine($"{typed,-18} want {want,-18} {verdict,-14} "
                      + $"wantFlags={info.Flags} wantFreq={(info.HasFrequency ? info.FrequencyRank.ToString() : "-")}");
    foreach (var c in ranked.Take(5))
    {
        var mark = string.Equals(c.Word, wantLower, StringComparison.OrdinalIgnoreCase) ? " <== want" : "";
        Console.WriteLine($"      {c.Score:F3}  {c.Word,-20} {c.Origin,-20} d={c.Distance} "
                          + $"flags={c.Info.Flags} freq={(c.Info.HasFrequency ? c.Info.FrequencyRank.ToString() : "-")}{mark}");
    }
}

index.Dispose();
