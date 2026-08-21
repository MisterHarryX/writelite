using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Rules;
using WriteLite.Services.Spelling;

// Diagnostic for the malformed-replacement cluster.
// Runs the SAME fast analyzer graph the external field monitor uses (App.xaml.cs) over a
// set of misspellings, and dumps every field of every finding plus the strings the card
// would actually render. Nothing here is a test; it exists to show what the pipeline does.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var spellChecker = new LocalSpellChecker();
Console.WriteLine($"dictionaries ru={spellChecker.Stats.RussianWordCount} en={spellChecker.Stats.EnglishWordCount}");

var fast = new CompositeTextAnalyzer(
    new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), spellChecker.RussianFormIndex),
    new SpellTextAnalyzer(spellChecker) { ContextualRefinementEnabled = false });

string[] texts =
[
    "роботает",
    "Сечас",
    "превет",
    "работет",
    "интиресный",
    "пожалуста",
    "сделаный",
    "Привет Алексей я хотел узнать сможешь ли ты завтра приехать в офис. Сечас программа роботает стабильней но некоторые ошибки всё ещё возникает.",
];

foreach (var text in texts)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"TEXT: {text}");
    var issues = await fast.AnalyzeAsync(text);
    if (issues.Count == 0) { Console.WriteLine("  (no findings)"); continue; }
    foreach (var issue in issues)
    {
        Dump(text, issue);
    }
}

// Raw suggestion lists straight from the lexicon, before any product filtering.
Console.WriteLine();
Console.WriteLine(new string('=', 78));
Console.WriteLine("RAW SUGGESTIONS");
foreach (var word in new[] { "роботает", "Сечас", "превет", "работет", "интиресный", "пожалуста", "сделаный", "стабильней" })
{
    var result = spellChecker.CheckWord(word, SpellingLanguage.Russian);
    var rendered = result.Suggestions.Select(s => s.Contains(' ') ? $"[{s}]<<SPACE>>" : s);
    Console.WriteLine($"  {word,-14} known={result.IsKnown,-5} -> {string.Join(" | ", rendered)}");
}


// ---------------------------------------------------------------------------
// Second pass: the shapes the fast lane above cannot show.
Console.WriteLine();
Console.WriteLine(new string('=', 78));
Console.WriteLine("MULTI-WORD REPLACEMENTS (single-token span, replacement contains a space)");
foreach (var word in new[] { "вообщем", "врятли", "потомучто", "какбудто", "вкурсе", "втечении", "наврятли" })
{
    var issues = await fast.AnalyzeAsync(word);
    foreach (var issue in issues) Dump(word, issue);
}

Console.WriteLine();
Console.WriteLine(new string('=', 78));
Console.WriteLine("RAW HUNSPELL (bypassing the form index) - looking for split-word candidates");
var hunspell = HunspellSpellingLexicon.LoadRussian();
Console.WriteLine($"  hunspell ready={hunspell.IsReady}");
foreach (var word in new[] { "роботает", "Сечас", "превет", "работет", "интиресный", "пожалуста",
                             "сделаный", "вобщем", "какбудто", "стобы", "необходимобыло", "непомню" })
{
    var raw = hunspell.Suggest(word, 8);
    var flagged = raw.Select(s => s.Any(char.IsWhiteSpace) ? $"<<SPLIT:{s}>>" : s);
    Console.WriteLine($"  {word,-16} -> {string.Join(" | ", flagged)}");
}


Console.WriteLine();
Console.WriteLine(new string('=', 78));
Console.WriteLine("END-TO-END: which tokens reach a card with whitespace in the replacement");
foreach (var word in new[] { "роботает", "Сечас", "пожалуста", "вобщем", "какбудто",
                             "необходимобыло", "непомню", "ро ботает" })
{
    var result = spellChecker.CheckWord(word, SpellingLanguage.Russian);
    var top = result.Suggestions.Count > 0 ? result.Suggestions[0] : "<none>";
    var flag = top.Any(char.IsWhiteSpace) ? "  <<< SPLIT REACHES THE CARD" : string.Empty;
    Console.WriteLine($"  {word,-16} known={result.IsKnown,-5} top={Q(top)}{flag}");
}

static void Dump(string text, TextIssue issue)
{
    var inRange = issue.Start >= 0 && issue.Length >= 0 && issue.Start + issue.Length <= text.Length;
    var slice = inRange ? text.Substring(issue.Start, issue.Length) : "<OUT OF RANGE>";
    var matches = string.Equals(slice, issue.Original, StringComparison.Ordinal);
    Console.WriteLine($"  rule={issue.RuleId} cat={issue.Category} start={issue.Start} len={issue.Length}");
    Console.WriteLine($"    slice     = {Q(slice)}{(matches ? "" : "   <<< SLICE != ORIGINAL")}");
    Console.WriteLine($"    Original  = {Q(issue.Original)}");
    Console.WriteLine($"    Replace   = {Q(issue.Replacement ?? "<null>")}");
    if (issue.Replacement is { } r && r.Any(char.IsWhiteSpace))
    {
        Console.WriteLine("    <<< REPLACEMENT CONTAINS WHITESPACE INSIDE A SINGLE-TOKEN SPAN");
    }
    Console.WriteLine($"    card.orig = {Q(CorrectionCardText.OriginalDisplay(issue))}");
    Console.WriteLine($"    card.repl = {Q(CorrectionCardText.ReplacementDisplay(issue))}");
    Console.WriteLine($"    chip      = {Q(CorrectionPresentation.FormatChipLabel(issue))}");
    Console.WriteLine($"    change    = {Q(CorrectionPresentation.FormatChange(issue))}");
}

static string Q(string value) => "«" + value + "»";
