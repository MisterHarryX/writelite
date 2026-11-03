namespace WriteLite.Services.Personal;

/// <summary>One calibration topic.</summary>
public sealed record CalibrationPrompt(string Title, string Hint);

/// <summary>
/// The first-run calibration: a few short texts the user writes by hand, so the library starts
/// from their own vocabulary rather than from nothing.
/// </summary>
/// <remarks>
/// <para>The topics are chosen to cover different registers — about oneself, a message to a
/// friend, a request at work or study, an opinion, and a free one — because people write
/// differently in each, and a library seeded only with chat would predict chat in a letter.</para>
///
/// <para>The texts are typed, not pasted. Pasted text is someone's prose, but not necessarily
/// the user's, and it carries none of their typing habits — the typos they make and correct are
/// exactly what the personal correction ranking learns from.</para>
/// </remarks>
public static class StyleCalibration
{
    /// <summary>Words a text must have before it counts.</summary>
    public const int MinimumWords = 40;

    /// <summary>The length the progress bar aims at.</summary>
    public const int TargetWords = 80;

    public static IReadOnlyList<CalibrationPrompt> Prompts { get; } =
    [
        new("Расскажите о себе",
            "Кто вы, чем занимаетесь, что любите. Пишите так, как рассказали бы новому знакомому."),
        new("Сообщение другу",
            "Напишите другу, как прошли выходные или что у вас нового. Обычным языком, как в мессенджере."),
        new("Просьба по работе или учёбе",
            "Попросите коллегу или преподавателя о чём-нибудь: перенести встречу, прислать файл, ответить на вопрос."),
        new("Ваше мнение",
            "Расскажите о книге, фильме или игре, которые запомнились, и объясните, чем именно."),
        new("Свободная тема",
            "О чём угодно: планы на неделю, воспоминание, идея. Чем естественнее, тем точнее WriteLite поймёт ваш стиль."),
    ];

    /// <summary>Words in a calibration text, counted the way the library tokenizes.</summary>
    public static int CountWords(string? text) => string.IsNullOrWhiteSpace(text) ? 0 : PersonalStyleModel.Tokenize(text).Count;
}
