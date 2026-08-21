# WriteLite Desktop — Design Audit & Design System

Stage 1 deliverable for the full UI/UX redesign. Reference surface:
<https://writelite-web.vercel.app/ru>.

The goal is a desktop application that reads as a native continuation of the
WriteLite website — same palette, same typographic voice, same editorial rhythm —
without copying the marketing page layout and without touching the correction
engines.

---

## 1. Existing application inventory

**Stack.** WPF on `net10.0-windows` (`src/WriteLite.App`), `UseWPF` +
`UseWindowsForms` (tray icon). Single window shell plus four always-on top-level
overlay windows. No MVVM framework: pages are `UserControl`s with code-behind,
data flows through explicit `Bind*` calls from `App.xaml.cs`.

**Shell.**

| Piece | File | Notes |
| --- | --- | --- |
| Main window | `Views/MainWindow.xaml` | Custom chrome via `WindowChrome`, 48 px title bar, 210 px nav rail, `ContentControl` page host |
| Logo | `Controls/WriteLiteLogo.xaml` | Hardcoded `#0B0B0C` / `#303035` / `#FF7A1A`, "W" in Segoe UI ExtraBold |

**Pages** (`Views/Pages`): `HomePage`, `EditorPage`, `DictionaryPage`,
`ExceptionsPage`, `SettingsPage`, `DiagnosticsPage`, `PlaceholderPage`
(reused three times: About, Local AI, Supported apps).

**Overlay windows** (`Views`): `SuggestionsWindow.xaml` (system-wide issue
panel), `BubbleWindow.xaml` (floating pen + count indicator),
`CorrectionPopupWindow.cs` and `LexicalPopupWindow.cs` (built entirely in C#),
`InlineErrorOverlayWindow.cs` (draws the underlines over the host application).

**Theme layer** (`Themes/*.xaml`, merged by `WriteLiteTheme.xaml`): already split
into `Colors / Typography / Spacing / Buttons / Inputs / Cards / Navigation /
Popups / Scrollbars / Tooltips / Icons / Animations`. The split is real but
mostly empty — `WriteLiteTheme.xaml` itself carried 419 lines of styles that
belong in the leaf dictionaries, and several dictionaries held a single style.

**What already works and must not regress.** UI Automation focus tracking, text
read/write through `ValuePattern`/`TextPattern`, the rule/spelling/grammar
analyzer chain, the local AI provider and Qwen client, the language-engine host,
lexical packs and morphology, user dictionary and ignore lists, settings
persistence, tray behaviour, DPI-aware overlay placement.

**Good news for the redesign.** Code-built windows resolve every brush through
`Services/ThemeResource.cs` (`ThemeResource.Brush("WlSurface", …)`), so
retokenising `Colors.xaml` propagates to the C#-built popups with no code change.

---

## 2. Problems found

**Palette is cool; the brand is warm.** The app used blue-grey neutrals
(`#0C0D0F`, `#171A20`, `#2B303A`, text `#F5F6F7`); the site uses warm near-black
and warm white (`#080808`, `#151514`, `#f1efec`). Side by side the app looked
like a different product.

**Wrong accent value.** App `#FF7A00`, site `#EC6C08`. The site keeps `#FF7A00`
only as `--wl-accent-bright` for hover.

**Radii too large.** App: 8–24 px, `WlPremiumCard` at 18 px, logo tile at 24 px.
Site: 5 px buttons, 6 px cards, 12 px large panels. The oversized radii were the
single biggest "generic AI dashboard" tell.

**Borders too heavy.** `#2B303A` is a solid, visible line. The site uses
`rgba(255,255,255,0.07)`.

**Off-brand semantic colours.** `WlCatStyle = #6F94C9` (blue) and
`WlCatSpelling = #E96B70` (coral) broke the "no random blue, orange stays the
accent" rule. The site's own wavy marks are `#ec6c08`, `rgba(232,150,69,.88)`
and `rgb(164,132,71)` — one warm family.

**No mono voice.** The site's most recognisable device — `01`, `ЛОКАЛЬНАЯ
ПРОВЕРКА`, `SHA-256` in JetBrains Mono with wide uppercase tracking — did not
exist in the app. `WlFontMono` was declared and used only for the strikethrough
original/replacement pair.

**Section numbers used decoratively, not systematically.** `01/02/03` appeared
once, inside a Home card, as bold orange 11 px sans — not as the quiet mono
`text-subtle` marker the site uses on every section.

**Pill overuse.** Status chips, count badges and the dictionary counter all used
`CornerRadius=999`. The site uses a pill only for the hero eyebrow.

**Type scale drift.** Page titles were 28 px in `WlPageTitle`, 34 px inline on
Home, 24 px hardcoded on Settings and Exceptions, 20 px on Editor and
Placeholder. Section labels were `WlSectionLabel` in some pages and inline
`FontSize=11 FontWeight=SemiBold` in others.

**Legacy inline styling.** `SettingsPage`, `ExceptionsPage` and `DiagnosticsPage`
styled `TextBox`/`CheckBox`/`ComboBox` inline instead of using `Inputs.xaml`;
`DictionaryPage` used a default `DataGrid` with `AlternatingRowBackground`;
`SettingsPage` was a wall of 20 default WPF check boxes.

**Emoji in the UI.** `📖` in the dictionary empty state, `✓`/`✕`/`×`/`→`/`●` as
text glyphs in five places.

**Decorative blob.** A 420 px orange `Ellipse` bleeding behind the page host in
`MainWindow` — exactly the "floating colourful blob" the brief rules out.

**Dead token.** `CorrectionPopupWindow` asks for `WlDanger`, which no dictionary
defines, so failure messages silently fell back to `Brushes.OrangeRed`.

**Motion.** Two duration tokens, no easing token, no shared transitions;
`WlReducedMotion` declared but unused.

---

## 3. Website design language (measured, not guessed)

Values below were read out of the live page's computed styles and CSS custom
properties.

### Colour

| Token | Value | Role |
| --- | --- | --- |
| `--wl-bg` | `#080808` | Page background |
| `--wl-bg-soft` | `#10100f` | Recessed / inset surface |
| `--wl-surface` | `#151514` | Card, panel |
| `--wl-surface-hover` | `#181817` | Hover surface |
| `--wl-text` | `#f1efec` | Primary foreground |
| `--wl-text-2` | `#a09d98` | Secondary |
| `--wl-text-3` | `#65625e` | Tertiary |
| `--subtle` | `#8a847b` | Mono labels, section numbers |
| `--wl-accent` | `#ec6c08` | Brand accent |
| `--wl-accent-bright` | `#ff7a00` | Hover accent |
| `--wl-accent-dark` | `#b94300` | Pressed accent |
| `--primary-foreground` | `hsl(25 76% 5%)` ≈ `#170c03` | Text **on** orange |
| `--wl-line` | `rgba(255,255,255,0.07)` | Hairline border |
| `--wl-line-soft` | `rgba(255,255,255,0.045)` | Quieter hairline |
| `--wl-line-accent` | `rgba(236,108,8,0.35)` | Accented border |
| `--border` | `hsl(0 0% 13%)` = `#212121` | Solid separator |
| `--success` | `hsl(152 30% 55%)` ≈ `#6fb593` | Muted sage, not neon green |
| `--destructive` | `hsl(8 72% 60%)` ≈ `#e3695a` | Warm coral |

Note the foreground on orange is **near-black**, not white.

### Typography

Inter for everything, JetBrains Mono for labels, numbers and technical strings.

| Role | Size / line | Weight | Tracking |
| --- | --- | --- | --- |
| Display (h1) | 72 / 74.9 | 500 | −0.035 em |
| Section title (h2) | 48 / 49.9 | 500 | −0.035 em |
| Card title (h3) | 17 / 25.5 | 500 | −0.015 em |
| Body | 16 / 24 | 400 | normal |
| Mono section number | 11 | 500 | −0.04 em |
| Mono eyebrow | 10–11, uppercase | 400 | **+0.18 – 0.22 em** |

Two observations that matter: display weight is **500, never 700**, and the big
sizes carry strongly *negative* tracking while the small mono labels carry
strongly *positive* tracking. That contrast is the house voice.

### Shape, line, motion

- Radii: `5px` buttons, `6px` cards, `2px` chips, `12px` large panels. Restrained.
- Borders: 1 px at 4.5–12 % white. Never a solid mid-grey.
- Buttons: 48 px tall CTA, 24 px horizontal padding, 15 px / 500, −0.01 em.
- Motion: `--dur-micro: 200ms`, `--ease-editorial: cubic-bezier(0.22,1,0.36,1)`,
  press feedback `transform 0.12s`.
- Section header: mono number + title on a baseline row with a hairline underneath.
- Error marks: `underline 1.5px wavy` — `#ec6c08` (grammar/spelling),
  `rgba(232,150,69,.88)` (punctuation), `rgb(164,132,71)` (style).

---

## 4. Desktop design system

### 4.1 Colour (`Themes/Colors.xaml`)

The site tokens land 1:1, plus desktop-only derivations the web page does not
need (title bar, rail, disabled, selection, category tints).

```
WlBg        #080808   window background        WlText          #F1EFEC
WlBgSoft    #10100F   rail / inset             WlTextSecondary #A09D98
WlSurface   #151514   card / panel             WlTextMuted     #65625E
WlSurfaceHi #181817   hover                    WlTextSubtle    #8A847B  (mono labels)
WlRaised    #1B1B19   input / raised
WlLine      #12FFFFFF (7 %)                    WlBrand         #EC6C08
WlLineSoft  #0BFFFFFF (4.5 %)                  WlBrandHover    #FF7A00
WlLineStrong#1FFFFFFF (12 %)                   WlBrandPressed  #B94300
WlLineAccent#59EC6C08 (35 %)                   WlOnBrand       #170C03
```

Semantic: `WlSuccess #6FB593`, `WlWarning #E89645`, `WlDanger #E3695A`,
`WlInfo #A09D98`. Category accents stay inside the warm family:
orthography `#EC6C08`, grammar `#E0A64B`, punctuation `#E89645`,
style `#A48447`, formatting `#8A847B`. `WlDanger` is now defined, closing the
dead-token bug.

### 4.2 Typography (`Themes/Typography.xaml`)

Font stacks degrade gracefully — Inter and JetBrains Mono are not installed on a
stock Windows box, so the declared families fall back to Segoe UI Variable and
Cascadia Mono, both of which have solid Cyrillic coverage:

```
WlFont        "Inter, Segoe UI Variable Text, Segoe UI"
WlFontDisplay "Inter, Segoe UI Variable Display, Segoe UI"
WlFontMono    "JetBrains Mono, Cascadia Mono, Consolas"
```

> Shipping the two OFL fonts inside the app would give exact parity with the web
> page. That needs the font binaries added to the repo, so it is left as a
> follow-up rather than done silently — see §6.

Scale (all weights 500 for headings, never bold):

| Style | Size / line | Use |
| --- | --- | --- |
| `WlDisplay` | 34 / 36 | Home hero only |
| `WlPageTitle` | 26 / 30 | One per page |
| `WlSectionTitle` | 17 / 24 | Card and group headings |
| `WlBody` | 14 / 21 | Default reading text |
| `WlBodyLarge` | 15 / 23 | Editor canvas, definitions |
| `WlSecondary` | 13 / 20 | Supporting copy |
| `WlCaption` | 12 / 17 | Meta, hints |
| `WlMonoLabel` | 10, uppercase, +0.18 em | Eyebrows |
| `WlMonoNumber` | 11 | `01`–`12` section markers |
| `WlMonoValue` | 12 | Hashes, paths, versions |

WPF has no `LetterSpacing`, so `Controls/TypographyTracking.cs` adds an attached
`wl:Type.Tracking` property that inserts hair/thin spaces between characters and
mirrors the original string into `AutomationProperties.Name` so screen readers
still read the real word.

### 4.3 Shape and spacing

Radii: `WlRadiusXs 4`, `WlRadiusSm 5` (controls, buttons), `WlRadius 6` (cards),
`WlRadiusMd 8`, `WlRadiusLg 12` (windows, popovers). Nothing larger.
Spacing scale: 4 / 8 / 12 / 16 / 24 / 32 / 48.

### 4.4 Components

Buttons — `WlPrimaryButton` (orange, `WlOnBrand` text, 32 px, r5),
`WlSecondaryButton` (transparent + hairline), `WlGhostButton` (text only),
`WlDangerButton`, `WlIconButton` (28 px), `WlWindowButton`, `WlCtaButton` (44 px
for the single hero action). Every button gets a 0.12 s press scale.

Surfaces — `WlCard` (surface + hairline + r6), `WlInsetPanel` (`WlBgSoft`),
`WlPopoverSurface` (r12 + the only real shadow), `WlSectionHeader` (mono number +
title + hairline rule).

Inputs — `WlTextInput`, `WlSearchInput` (leading icon), `WlComboBox`,
`WlCheckBox` (custom 16 px square with orange check), `WlToggleSwitch` (36×20
track), `WlSettingRow` (label + description + control, hairline separated).

Correction — `WlCorrectionCard`, `WlCategoryLabel` (mono uppercase in the
category colour), `WlChangeRow` (mono strikethrough original → mono orange
replacement), explanation, action row. Same hierarchy as the website card.

Navigation — `WlNavItem`: 32 px, mono index, icon, label; selected state is a
2 px orange left indicator + `WlSurface` background + `WlText` foreground.

Icons — one outline family in `Themes/Icons.xaml`, all authored on a 24×24 box
with 1.5 px strokes and round caps, matching the site's Lucide-style line work.

### 4.5 Motion

Tokens live in `Themes/Animations.xaml` and their code-side twins in
`Services/Motion.cs`: `WlDurPress 90ms`, `WlDurRelease 150ms`, `WlDurHover 140ms`,
`WlDurPopover 160ms`, `WlDurMicro 200ms`. Every one of them runs on the website's
curve, `cubic-bezier(0.22, 1, 0.36, 1)`. WPF has no cubic-bezier easing and its
built-ins are all weaker, so the curve is implemented once as
`Services/EditorialEase.cs` and each dictionary declares its own
`<wl:EditorialEase x:Key="WlEase" />` — an animation inside a control template
cannot reference a resource in a sibling merged dictionary.

Four rules hold everywhere:

- **Only `Opacity` and `RenderTransform` animate.** Both are composited, so no
  transition in the product can trigger a layout pass. Hover states fade a
  dedicated overlay layer rather than changing a brush, which also keeps every
  colour in `Colors.xaml` instead of restating hex values inside animations.
- **Initial state is not animated.** A control that is already selected when its
  page opens has not changed; opening Settings must not replay twenty toggles.
  Templates pair a plain trigger carrying the resting value with a `MultiTrigger`
  that also requires `Motion.IsSettled`, and put the storyboard on the second one.
- **Reduced motion is honoured**, from `SystemParameters.ClientAreaAnimation`.
  Transitions are skipped and the final value set directly, not played slowly.
- **Nothing loops** except the player's equalizer, which is stopped — not merely
  hidden — the moment playback pauses.

Press feedback is `wl:Motion.Press` on the style rather than a storyboard pasted
into each template, which is why it now reaches all eight button styles instead of
the two that happened to have been given a copy.

No blur, no glow; the only `Effect` in the system is one popover shadow.

---

## 5. Screen structure

```
┌──────────────────────────────────────────────────────────────┐
│ ▪ WriteLite   Редактор            ● Локально   RU   ─  ✕     │  44 px title bar
├────────────┬─────────────────────────────────────────────────┤
│ 01 Главная │                                                 │
│ 02 Редактор│   writing canvas (max 720 px)  │ 03 Замечания   │
│ 03 Словарь │                                │  correction    │
│ 04 Слова   │                                │  cards         │
│ 05 AI      │                                │                │
│ ─────────  │                                │                │
│ 06 Настрой │                                │                │
│ 07 Диагно  │                                │                │
│ ▪ локально │                                │                │
└────────────┴────────────────────────────────┴────────────────┘
```

Navigation carries the mono index, so the site's numbering is structural rather
than decorative. Below 1080 px the suggestions panel collapses to a toggle;
below 900 px the rail drops to icon-only. The editor is never the thing that
gives way.

Settings mirrors the same numbering: `01 Основные`, `02 Проверка`,
`03 Языки`, `04 Словари`, `05 Локальный AI`, `06 Приватность`, `07 Приложение`.

---

## 6. What the redesign changed structurally

Three changes go beyond restyling, because the old structure could not carry the new
navigation. They are listed here rather than buried in the diff.

**Exceptions merged into a word manager.** `ExceptionsPage` (ignored words, ignored
rules) and the word list on the dictionary page answered the same question — "what
should WriteLite leave alone?" — from two different screens. They are now
`WordManagerPage` (`04`), three tabs over one list surface. `MainWindow.RefreshDictionary`
and `RefreshExceptions` keep their names and signatures, so `App.xaml.cs` is unaffected.

**The dictionary became a workspace.** `DictionaryPage` (`03`) is word lookup and
nothing else: a pinned search header over a scrolling article, with synonyms, antonyms
and translations rendered as `WordChip` buttons that open their own entry, and browser
back / forward over the trail. Lookup calls the existing `ILexicalKnowledgeService` —
the same offline service behind the double-click word card — through
`MainWindow.BindLexical`.

The pack catalogue is gone from this page entirely; see below.

**Built-in dictionaries left the interface.** The old catalogue listed WriteLite's own
morphological pack, explanatory pack, language-engine tree and historical sample with
their file sizes, licence strings and paths, and offered Delete / Disable / Verify on
all of them — actions the service refused for built-ins, so the buttons could only ever
produce an error dialog. `LexicalPackCatalogService.UserPacks()` now returns only what
the user installed themselves, and that is what «Мои словари» (a fourth tab in the word
manager) shows. `Snapshot()` survives for diagnostics, where the full picture is the
point. It is also much cheaper: the old page parsed the 55 MB open pack and walked the
whole language-engine tree to draw rows nobody could act on.

**English translations exist at all.** The Russian and English packs each describe one
language; nothing connected them. `resources/lexical/ru-en-translations.db` (built by
`ai/scripts/build_ru_en_translations.py` from OpenRussian's `translations_en` column,
the same pinned release the Russian pack comes from) carries 90 794 pairs in both
directions, read through `TranslationIndex`. SQLite rather than JSON for the reason the
packs themselves moved: as JSON this index costs about 20 MB of heap. Nothing here is
generated — a fabricated translation is indistinguishable from a real one until someone
relies on it.

**The rail plays the website's ambience.** `Controls/AmbienceBar` plus
`Services/Audio/AmbiencePlayer` sit at the foot of the navigation rail: transport,
track, a 2 px progress rule, volume and a four-bar equalizer. The bundled track is the
site's own `dark-ambient-soundscape.mp3`; anything the user drops in
`%LocalAppData%/WriteLite/audio` joins the same list, which is what makes previous and
next mean something. It hides itself when there is nothing to play and never starts on
its own.

**A real local-engine page.** `05` was a `PlaceholderPage` with a paragraph. It is now
`LocalEnginePage`, a read-only status view: engine state, model, dictionaries, network.
Editing still lives in Settings, and it links there rather than duplicating controls.

Two presentation helpers were extracted while doing this, both to delete duplication
rather than to add abstraction: `CorrectionCardText` (card formatting, previously copied
between the editor and the system panel) and `IssueWaveGeometry` (the wavy mark, now
identical in-app and over other applications).

---

## 7. Validation

Built and run on Windows 11, and every screen inspected in the running application.

| Check | Result |
| --- | --- |
| Build | Clean, 0 warnings |
| Test suite | 456 / 464 pass, 3 skipped, 5 pre-existing failures (below) |
| Every page renders | Covered by `PageRenderSmokeTests` |
| Every theme resource realises | Covered by `DesignSystemTests` |
| Navigation, page titles, selection state | Verified in the running app |
| Cyrillic rendering | Verified at every type size, including tracked mono labels |
| Editor underlines, correction cards, filters | Verified with live analyzer output |
| Dictionary lookup and empty state | Verified against the installed pack |
| Responsive behaviour | Verified at 1280 px and 880 px |
| Floating indicator | Verified over another application |
| UI Automation monitoring still works | Confirmed via the diagnostics page while monitoring a live field |

The 5 failing tests fail identically on the pre-redesign commit and are environmental:
three need a `bin/Release` build that does not exist in this working tree, and two assert
that the AI layer falls back when no local model is reachable, on a machine where the
`writelight-qwen` pack is installed and a server is listening on `127.0.0.1:8742`.

### Bugs found and fixed during validation

- **App crash on a grouped word list.** `WlListGroupHeader` used
  `BasedOn="{StaticResource WlMonoLabel}"` across merged dictionaries. WPF realises
  dictionary entries lazily, so the reference resolved during development and threw
  `XamlParseException` the first time the list was actually shown, taking the process
  down. Cross-dictionary references are now `DynamicResource` only, and
  `Every_resource_in_the_theme_can_be_realised` fails the build if one comes back.
- **"23 слов".** The word counter tested `count is >= 2 and <= 4`, which is right for
  2–4 and wrong for 22–24. Russian agreement now lives in `RussianPlural`, with tests.
- **Pages opened scrolled.** Focusing the window handed focus to the first control inside
  the page, whose `BringIntoView` scrolled the heading off. Focus now lands on the rail
  and pages reset to the top on navigation.
- **Filter tabs clipped.** A `WrapPanel` inside a horizontally scrolling `ScrollViewer`
  is measured against infinite width and never wraps, cutting off the last tab in both
  the editor panel and the system panel.
- **Disabled primary button read as muddy brown.** A 40 %-opacity orange over near-black
  looks like a rendering fault; disabled now drops to a neutral surface.

---

## 8. Deliberate scope boundaries

- No analyzer, language-engine, lexical, settings-store or UIA code is rewritten.
  The redesign is confined to `Themes/`, `Views/`, `Controls/`, plus the two
  presentation-only services (`IssueUnderlineStyle`, `ThemeResource`).
- Inter / JetBrains Mono are **not** bundled: that needs font binaries committed
  to the repo, which is a licensing and repo-size decision for the maintainer.
  The font stacks are already written so that dropping the files in is the only
  remaining step.
- Light theme is out of scope; the product is dark-only, as the website is.
- The click-to-correct popover is laid out for a list of alternative replacements, but
  `TextIssue` carries a single `Replacement`, so it shows one. Surfacing the spell
  checker's other candidates is an analyzer change, not a visual one, and was left alone.

## 9. Suggested next steps

1. **Bundle Inter and JetBrains Mono.** The font stacks already name them first; adding
   the OFL files and registering them makes the desktop typography identical to the web
   page. This is the single largest remaining gap to full parity.
2. **Carry multiple spelling suggestions into `TextIssue`** so the correction popover can
   offer alternatives, as the layout already allows.
3. **Extend `PageRenderSmokeTests` to the overlay windows** (suggestions panel, bubble,
   correction and lexical popovers). They are built in code rather than XAML, so they are
   less exposed to the lazy-realisation trap, but the coverage is cheap.
