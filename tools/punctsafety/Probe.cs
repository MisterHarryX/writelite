namespace PunctSafety;

/// <summary>
/// Correctly punctuated Russian, in the shapes a comma classifier is most likely to spoil.
/// </summary>
/// <remarks>
/// <para>Hand-written in-repo for this probe. Every sentence needs no further comma, so the
/// correct output for the whole file is zero findings and any number above zero is a measured
/// cost rather than a judgement call.</para>
///
/// <para>The categories are not arbitrary. Each is a place where the training distribution and
/// the deployment distribution differ:</para>
/// <list type="bullet">
/// <item><b>short</b> — the corpus filtered to sentences of 20–300 characters, so the model has
/// barely seen anything shorter, and short sentences almost never take a comma.</item>
/// <item><b>informal</b> — the corpus is 60 % news and formal prose. Chat has different clause
/// structure and far lighter punctuation.</item>
/// <item><b>technical</b>, <b>mixed script</b> — Latin tokens inside Russian are common in real
/// use and rare in a Wiktionary-derived corpus; the tokenizer sees them as unknown pieces.</item>
/// <item><b>abbreviations</b> — «т. д.» and «и т. п.» contain periods and spaces that look like
/// clause boundaries to anything working on surface form.</item>
/// <item><b>names</b> — a capitalised sequence resembles an appositive, which is a genuine
/// comma context, so this is where a model most plausibly over-generalises.</item>
/// <item><b>quotes</b>, <b>enumeration</b> — already-punctuated structures where an extra comma
/// is both easy to propose and clearly wrong.</item>
/// <item><b>already correct</b> — ordinary prose that already has all the commas it needs; the
/// model must not add a second one beside the first.</item>
/// </list>
/// </remarks>
internal static class Probe
{
    public static readonly (string Category, string[] Sentences)[] Corpus =
    [
        ("short", [
            "Он пришёл домой.",
            "Всё готово к отправке.",
            "Завтра будет дождь.",
            "Она читает книгу.",
            "Мы уже обсудили это.",
            "Файл не найден.",
            "Проверь почту.",
            "Работа сделана в срок.",
        ]),

        ("informal", [
            "Слушай, я сегодня не смогу прийти.",
            "Привет, как дела?",
            "Ну ладно, тогда до встречи.",
            "Я думаю, что это хорошая идея.",
            "Он сказал, что придёт вечером.",
            "Мне кажется, ты прав.",
            "Спасибо большое за помощь.",
            "Сегодня было очень много работы.",
        ]),

        ("technical", [
            "Запусти npm run build и проверь результат.",
            "Ошибка возникает в методе GetUserById при пустом идентификаторе.",
            "Скопируй файл config.json в папку resources.",
            "Сервер отвечает кодом 500 на каждый запрос.",
            "Открой GitHub и создай новую ветку.",
            "Этот middleware обрабатывает все входящие запросы.",
            "Обнови зависимости через pip install -r requirements.txt.",
        ]),

        ("mixed script", [
            "Мы используем Docker для локальной разработки.",
            "Приложение написано на C# и работает под Windows.",
            "Модель Qwen отвечает быстрее предыдущей.",
            "Подключи VPN перед началом работы.",
            "Скачай Visual Studio Code с официального сайта.",
        ]),

        ("abbreviations", [
            "Книги, тетради и т. п. лежали на полке.",
            "Отменили, т. к. был сильный дождь.",
            "В работе участвовали проф. Иванов и доц. Петров.",
            "Стоимость составляет 1500 руб. за единицу.",
            "См. приложение № 3 к настоящему договору.",
        ]),

        ("names", [
            "Александр Сергеевич Пушкин родился в Москве.",
            "Мария Ивановна работает в нашем отделе.",
            "Компания Яндекс открыла новый офис.",
            "Иван Петров и Сергей Сидоров подписали документ.",
            "Роман «Война и мир» написал Лев Толстой.",
        ]),

        ("quotes", [
            "Он сказал: «Я приду завтра утром».",
            "В статье «Новые методы анализа» описан этот подход.",
            "Она прошептала: «Спасибо».",
            "Заголовок «Итоги года» набран крупным шрифтом.",
        ]),

        ("enumeration", [
            "На столе лежали книги, тетради и ручки.",
            "Нужно купить хлеб, молоко, сыр и масло.",
            "Он быстро, уверенно и точно ответил на вопрос.",
            "В отчёте есть введение, основная часть и заключение.",
        ]),

        ("already correct", [
            "Когда он пришёл домой, все уже спали.",
            "Я думаю, что завтра будет хорошая погода.",
            "Несмотря на дождь, мы вышли на улицу.",
            "Он сделал всё, что от него требовалось.",
            "Если вы согласны, подпишите документ.",
            "Книга, которую я читаю, очень интересная.",
            "Договор был подписан вчера, и работа началась сегодня.",
            "Прочитав письмо, он молча вышел из комнаты.",
        ]),

        ("no comma needed", [
            "Вчера вечером мы гуляли по набережной.",
            "В прошлом году компания увеличила прибыль на двадцать процентов.",
            "Все сотрудники отдела получили новые компьютеры.",
            "Этот подход позволяет существенно сократить время обработки данных.",
            "Наша команда работает над проектом уже несколько месяцев.",
            "Результаты исследования будут опубликованы в следующем квартале.",
            "Он внимательно прочитал документ и поставил свою подпись.",
            "Студенты университета приняли участие в международной конференции.",
        ]),
    ];
}
