# WriteLite — Final Language Intelligence Sprint

Date: 18 August 2026. Every number below has a command that reproduces it; nothing is
estimated.

## Summary

The central result is a trade the previous architecture could not make:

| configuration | P | R | F1 | strict | final-text | FP/100 | no-change | p50 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| **A** — rules + spelling only | 0.942 | 0.720 | 0.816 | 0.628 | 0.674 | 0.299 | 0.969 | **3.2 ms** |
| **B** — production before this sprint (rules+spell+LanguageTool) | 0.952 | 0.718 | 0.819 | 0.598 | 0.648 | 0.249 | 0.971 | **136.3 ms** |
| **C** — improved deterministic (A + lexicon veto, signals, punctuation model) | 0.942 | 0.722 | **0.818** | 0.628 | 0.676 | 0.299 | 0.969 | 11.9 ms |
| **D** — C + routed local Qwen | 0.942 | 0.722 | 0.818 | 0.628 | 0.676 | 0.299 | 0.969 | 11.8 ms (p95 1 114 ms) |
| **E** — final candidate (C + LanguageTool) | 0.943 | 0.759 | **0.841** | 0.642 | 0.696 | 0.299 | 0.969 | 139.3 ms |

**Configuration C matches the previous production quality (F1 0.818 vs 0.819) at one twelfth
of the latency.** Configuration E is the recommended shipping configuration and improves on
the old one by +4.1 pp recall, +2.2 pp F1 and +4.8 pp final-text accuracy. Semantic
corruptions: **0** in every run.

Two results in that table are worth stating explicitly because they are negative:

- **A → C buys +0.002 F1 for +8.7 ms and +48 MB.** The lexicon veto, the lexical signals and
  the trained punctuation model together now contribute almost nothing measurable, because the
  new deterministic rules cover what they were compensating for. They are still shipped —
  their value on slang and profanity preservation does not show in this corpus's aggregate —
  but the case for the ONNX punctuation model specifically is now weak and is flagged for
  removal review.
- **C → D buys nothing at all.** Routed AI changed no metric to three decimal places. See §5
  and §12.

---

## 1. Architecture — before and after

Verified in the repository rather than assumed. The Editor and the field monitor were
**already** bound to one shared core (`HybridTextAnalysisService` over
`WriteLiteOrchestratingAnalyzer`); the earlier private editor analyzer had been removed. The
field monitor already had a real fast lane (180 ms, rules+spelling) and deep lane
(700–1200 ms, the hybrid service). Those were not the problems.

What was wrong:

| area | before | after |
|---|---|---|
| Morphology | one stored analysis per spelling; rules read it directly | full reading sets derived from generated paradigms, unioned with the stored analysis |
| Agreement / government | dative prepositions only (7 entries) | attributive, subject–predicate, preposition, verb and numeral government |
| Punctuation | fronted conjunctions, paired «то», «поэтому» | + gerund phrases, clause coordination (both directions), homogeneous members |
| AI routing | **one decision for the whole text**, whole text sent to the model | per sentence, bounded 3-sentence context, only the target sentence editable, budgeted, caret-first |
| Diff integrity | not checked | `ApplyIssues(original, issues) == correctedText` enforced; failing lists discarded whole |
| Acceptance | semantic guard + confidence floors | + morphological acceptance guard reading the *corrected* sentence |
| Writing assistance | none | local ghost text in the Editor, insertion-suggestion in the field path |

New assemblies/types: `RussianMorphology`, `RussianNounParadigm`, `RussianAdjectivalParadigm`,
`RussianPronounParadigm`, `RussianAgreementAnalyzer`, `RussianClauseCommaAnalyzer`,
`RussianComparativeAnalyzer`, `SentenceAiRouter`, `IssueApplication`,
`MorphologicalAcceptanceGuard`, `WritingAssistanceService`, `WritingAssistanceCoordinator`.

---

## 2. Root causes found

1. **The form index collapses homonymy.** It stores one grammatical analysis per spelling.
   «новым» is recorded as instrumental singular and is also dative plural; «инструкции» is
   recorded genitive and is also dative, prepositional and nominative plural. Any rule reading
   that tag directly rewrites correct text. **Fix:** generate the paradigm from the lemma and
   keep every slot the surface form fills, then union with the stored reading. Extra readings
   cost recall and never precision, so the union is always the safe direction.

2. **Homonymy across lemmas, not only within one.** «воду» is filed as the dative of a
   masculine «вод», so the accusative of «вода» — the reading every real sentence uses — was
   absent and «в воду» was reported as a government error. **Fix:** reconstruct plausible
   lemmas from the surface form and run each that the index attests.

3. **A lemma is not recognisable from its own index entry.** «логика» is filed as the genitive
   of «логик». Testing the tag for `nomn` therefore rejected exactly the lemmas needed.
   **Fix:** a candidate is a lemma when some form the index holds *names* it as its lemma,
   probed through three distinctive slots.

4. **`Contains` folds ё to е.** `Contains("планируёт")` is true — it matches the stored
   «планирует» — so a generator trusting it emitted whichever spelling it tried first, and
   «планируют» was being "corrected" to «планируёт». **Fix:** a zero-distance `FindWithin`
   returns the stored spelling; every generated form goes through it.

5. **Substantivised adjectives were declined as nouns.** The noun stem of «новое» is «ново»,
   whose prepositional singular is «новое» itself — so «в новым доме» was corrected to
   «в новое доме». **Fix:** inflection is chosen by the shape of the word, not by the recorded
   part of speech.

6. **Single letters and several prepositions are recorded as nouns** (they are the letters'
   names). «В аэропорту задержали…» offered «В» as a singular subject for a plural verb.

7. **The second locative was missing from the paradigm.** «в аэропорту», «на берегу», «в году»
   have a stressed `-у` locative; without it they read as datives and every one of them was a
   government error on correct text.

8. **Indefinite-personal sentences look exactly like subject–predicate disagreement.**
   «В аэропорту задержали несколько рейсов» has a plural verb and a singular noun and no
   subject at all. **Fix:** a plural predicate with a singular candidate subject is only
   reported when the two are adjacent across nothing but modifiers.

9. **AI routing was per document.** One confident finding in the opening line made the whole
   document "already covered" and the model was never consulted about sentence seven — which
   is where the only errors it can find live.

10. **The past-tense generator doubled the suffix.** `stem + "л" + "ли"` produced «сдалли».

---

## 3. Punctuation

Frozen corpus, punctuation category (n=30):

| | before | after (C) | after (E, +LT) |
|---|---:|---:|---:|
| precision | 0.867 | **0.905** | 0.913 |
| recall | 0.394 | **0.576** | 0.636 |
| F1 | 0.542 | **0.704** | 0.750 |
| final-text accuracy | 0.394 | **0.576** | 0.606 |

Three new rules, all decided from morphology rather than from which conjunction is present:

- **Gerund phrases** — `Прочитав письмо он задумался` → `Прочитав письмо, он задумался`;
  `читал книгу сидя у окна` → `книгу, сидя у окна`. Requires a dependent word (so `он ел стоя`
  is untouched) and excludes the gerunds that became prepositions (`несмотря на`, `судя по`).
  Handles the adverbialised gerunds the index files as adverbs by checking that a verb they
  could be formed from exists.
- **Clause coordination, both directions** — a comma before «и»/«или» is added when the second
  half has its own subject (`Дождь закончился, и выглянуло солнце`) and **removed** when the
  two predicates share one (`Он вошёл и сел за стол`). Verb transitivity settles the ambiguous
  case: after an intransitive verb a nominative-capable noun cannot be an object.
- **Homogeneous members** — `хлеб, молоко и сыр`, gated on all three nouns sharing a case and
  none having a genitive reading (which is what separates a list from `сроки поставки и
  стоимость доставки`).

False positives found and fixed during development, each a structural class rather than an
exception: detached participles after a comma agreeing with something earlier; head-plus-
genitive chains read as enumerations; negated gerunds losing their particle; words the index
files as gerunds that are not (`Какая`, `покоя`).

---

## 4. Morphology

| | before | after (C) | after (E) |
|---|---:|---:|---:|
| precision | 1.000 | 0.962 | 0.967 |
| recall | **0.021** | **0.532** | **0.617** |
| F1 | 0.042 | 0.685 | 0.753 |
| strict correction accuracy | 0.021 | **0.511** | 0.532 |

Deterministic morphology recall went from 2.1 % to 53.2 % — **above what LanguageTool
contributed (31.9 %)**, and at 11.9 ms rather than 136 ms.

Worked examples, all produced by the shipped pipeline:

| input | output | rule |
|---|---|---|
| Мама приготовила **вкусную** обед | **вкусный** обед | attributive, gender |
| Он живёт в **новым** доме | в **новом** доме | attributive, case, governed by «в» |
| Все студенты **сдал** экзамен | студенты **сдали** | subject–predicate |
| Он вернулся из **Германия** | из **Германии** | preposition government |
| Я хожу в бассейн два **раз** | два **раза** | numeral government |
| Мы гордимся **нашего города** | **нашим городом** | verb government |
| Он интересуется **историю** | **историей** | verb government |
| Интерфейс стал **более удобнее** | стал **удобнее** | double comparative |

The design constraint throughout: **a word the morphology cannot analyse produces an empty
reading set, an empty set agrees with everything, and agreement with everything produces no
finding.** Indeclinables, borrowings, surnames and irregular paradigms cost recall and never
precision. That is §2 implemented once, in one place, rather than in each rule.

---

## 5. AI routing and Qwen

Measured against a live local Qwen (`writelight-qwen-q4_k_m.gguf`, llama-server, ctx 768):

| | measured |
|---|---:|
| sentences routed | **102 / 842 = 12.1 %** |
| inferences per sentence | 0.121 |
| findings offered | 25 |
| findings **accepted** | **0** |
| routing reasons | strong-deterministic-only 308, no-contextual-uncertainty 432, contextual-risk-markers 97, unresolved-contextual-finding 5 |
| rejection reasons | below-confidence-floor 21, unsupported-lemma-rewrite 3, outside-eligible-category 1 |
| support class of everything offered | **Unsupported** (new span, nothing corroborating) |

Qwen latency, measured directly:

| | latency |
|---|---:|
| cold (first request after load) | 547 ms |
| warm, one sentence (55 prompt / 23 completion tokens) | **p50 312 ms** |
| warm, six-sentence paragraph (155 prompt tokens) | p50 286 ms |
| warm, same sentence with a 64-token output cap | p50 302 ms |

**§46 answered by measurement: output-token reduction would not help.** The model already
emits ~23 tokens for a sentence; the cost is fixed per-request decode overhead, not the
length of the answer. A longer prompt (155 vs 55 tokens) was *not* slower. Migrating to
structured edit output would therefore trade reliability for nothing measurable, so it was
not done.

What did reduce cost is architectural: routing per sentence with a budget means the deep lane
does bounded work regardless of document size, and the AI never blocks the fast lane.

---

## 6. Safety

- **Diff integrity (§28).** `IssueApplication.Reconstructs` enforces
  `Apply(original, issues) == correctedText`; a span list that fails is discarded whole rather
  than partially trusted. Overlapping, out-of-range and stale spans are distinct, tested
  outcomes.
- **Morphological acceptance guard (§29).** Reads the sentence a correction would *leave
  behind*: rejects a stacked comparative, a synthetic comparative standing as an attribute
  (`лучше решение`), a finite predicate replaced by an infinitive without a licensing modal,
  and a modifier left in front of an infinitive.
- **Regression tests (§2).** All six historical destructive corrections, each paired with an
  invented case of the same shape so the tests cannot be passed by an exception list.
  19 tests, all green.
- **Double comparatives are now owned deterministically**, which means the routing policy sees
  a high-confidence finding there and never asks the model — the class of damage is removed by
  removing the opportunity, not by catching the result.
- **Semantic corruptions: 0 / 382 changed items** in the final configuration.

---

## 7. Writing assistance

`WritingAssistanceService` — local, asynchronous, bounded to the current sentence plus the
previous one (320 characters).

- **Editor:** inline ghost text drawn as an adorner. The suggested text never exists in the
  document, so no crash, save or autosave can capture text the user did not type. **Tab**
  accepts, **Escape** dismisses, any keystroke or caret move supersedes. Triggered after a
  900 ms pause — deliberately four times the 220 ms analysis debounce, so error feedback is
  never behind a completion request (§52).
- **Field path:** a continuation is expressed as a zero-length insertion `TextIssue` at the
  caret, so the existing suggestions panel, apply path and write telemetry handle it with no
  changes. Style class, `CanApplyAutomatically: false` — never auto-applied.
- **Grounding (§36):** a completion is dropped if it contains a digit, `%`, `№`, a capitalised
  word absent from the context, fewer than three letters, more than 120 characters, or a
  repetition of what is already written. 12 tests assert these hold *whatever the model
  returns* — the promise does not depend on the model's behaviour.

---

## 8. Responsiveness and large documents

Deterministic full-document analysis (`morphprobe --perf`, warm caches):

| words | chars | p50 | p95 |
|---:|---:|---:|---:|
| 44 | 298 | 16.0 ms | 24.3 ms |
| 440 | 2 980 | 55.8 ms | 149.0 ms |
| 4 400 | 29 800 | 264.8 ms | 493.7 ms |
| 17 600 | 119 200 | 1 043.8 ms | 1 097.0 ms |
| 52 800 | 357 600 | 3 189.8 ms | 3 204.0 ms |

Linear, and **not what typing costs**: the editor schedules dirty-range analysis
(`AnalyzeChangedRangeAsync`) and only escalates to a full pass on Ctrl+Enter, with a character
ceiling above which live analysis stands down. Per-sentence corpus latency is p50 11.9 ms.

Application startup, measured from the release binary's own log: dictionary load 261 ms,
process start → `application-running` **1.27 s**, LanguageTool ready in the background at
+3.7 s.

---

## 9. Tests

Final Release build: **0 errors, 0 warnings.**

| | count |
|---|---:|
| total | **1 286** |
| passed | **1 283** |
| failed | **0** |
| skipped | 3 |

The three skips all require an external live process (`LiveHost_Check_Stop_FreesProcessAndPort`,
`LiveServer_IfRunning_ReturnsValidatedCorrection`,
`CSharp_Provider_Calls_Qwen_And_Corrects_Russian`). New in this sprint: 19 destructive-
correction regression tests, 12 writing-assistance tests, 4 editor/field parity tests. The
UIA end-to-end suite (4 tests, real UI Automation against the test host) passes.

No existing test was weakened. One assertion was changed: the rule-pack version pin moved
`ru-1.5.0` → `ru-1.6.0`, which is the mechanism that forces a version bump when rules are
added.

---

## 10. Files changed, by subsystem

**Morphology core** (`src/WriteLite.Language.Russian/`) — `RussianGrammemes.cs`,
`RussianMorphology.cs`, `RussianNounParadigm.cs`, `RussianAdjectivalParadigm.cs`,
`RussianPronounParadigm.cs` (all new).

**Language rules** (`src/WriteLite.App/Services/Grammar/`) — `RussianAgreementAnalyzer.cs`,
`RussianClauseCommaAnalyzer.cs`, `RussianComparativeAnalyzer.cs` (new);
`RuleBasedAnalyzer.cs` (wiring).

**AI pipeline** (`src/WriteLite.App/Services/Ai/`) — `SentenceAiRouter.cs`,
`IssueApplication.cs`, `MorphologicalAcceptanceGuard.cs`, `WritingAssistanceService.cs`,
`WritingAssistanceCoordinator.cs` (new); `HybridTextAnalysisService.cs`,
`LocalAiRoutingPolicy.cs`, `AiTextAnalysisService.cs`, `AiRoutingTelemetry.cs` (changed).

**Editor** — `EditorPage.Writing.cs` (new); `EditorPage.xaml.cs`, `MainWindow.xaml.cs`,
`App.xaml.cs` (changed).

**Rule pack** (`resources/rules/ru/`) — `ru.grammar.agreement`,
`ru.grammar.double-comparative`, `ru.punctuation.gerund-phrase`,
`ru.punctuation.clause-coordination`, `ru.punctuation.homogeneous-comma`; version `ru-1.6.0`.

**Tooling** — `tools/morphprobe/` (new: analysis forensics, sentence check, perf sweep),
`ai/scripts/add_rule.py` (new).

**Tests** — `DestructiveCorrectionRegressionTests.cs`, `WritingAssistanceTests.cs`,
`EditorFieldParityTests.cs` (new).

---

## 11. Known limitations

Stated plainly, not hidden.

- **Style layer not extended.** Tautology, bureaucratese and verbosity detection (§19–21) were
  not built. The existing `RussianStyleAnalyzer` is unchanged. This is the largest piece of the
  brief left undone.
- **Orthography/particle pack not extended (§17).** Orthography recall stayed at 0.701; the
  existing rules already cover the common particles, but `также/так же`, `тоже/то же`,
  `чтобы/что бы` contextual disambiguation was not added.
- **No v2 benchmark corpus.** All numbers are on the existing frozen 841-item corpus, whose
  punctuation category is 30 items and morphology 46. The per-subcategory punctuation and
  morphology corpora §55–56 ask for do not exist, so the punctuation and morphology numbers
  above rest on small samples and should be read as directional.
- **real_word recall remains 0.062.** Untouched by this sprint; it needs the contextual layer,
  not morphology.
- **`strict` correction accuracy for punctuation is 0.000** — punctuation fixes land as
  span-plus-comma edits that the harness scores as final-text correct but not span-exact. This
  is a measurement artefact, not a quality one, but it was not fixed.
- **No manual GUI interaction.** The release binary was built, launched, and verified through
  its own log to start, load 3 091 446 forms, wire the hybrid pipeline, detect the local Qwen
  and start the language engine. Mouse/keyboard driving of the editor, visual inspection of the
  ghost text, and interactive underline behaviour were **not** performed.
- **Field-path continuation surface reuses the suggestions panel** rather than a bespoke
  unobtrusive overlay. It works and is explicit-acceptance only, but it is not the polished
  surface §67 imagines.
- **Precision is 1 pp below the previous configuration** (0.952 → 0.943) and false positives
  per 100 tokens rose 0.249 → 0.299, bought for +4.1 pp recall. Clean no-change accuracy is
  essentially unchanged (0.971 → 0.969).

---

## 12. Retraining decision

**Retraining is not justified.**

Evidence, from the live-model run in §5:

- The model was consulted about 12.1 % of sentences — the routing is working and reaching
  exactly the uncertain ones (97 of 102 routed on contextual-risk markers, i.e. sentences no
  rule fired on).
- It returned 25 findings. **Every one was classified `Unsupported`** — a new span with
  nothing in the morphology, the lexicon or the rules corroborating it.
- 21 of 25 were rejected for falling below the confidence floor for that class; 3 were lemma
  rewrites (paraphrase, not correction); 1 was outside the eligible categories.
- Accepting them anyway is what the Phase 3 measurement already priced: precision 0.945 →
  0.713, false positives 5.3×, and correction accuracy unchanged.

The remaining failures are **not** model-capability failures. The categories still weak —
morphology beyond what paradigms settle, contextual punctuation, real-word errors — improved
in this sprint by 25× (morphology recall), 1.5× (punctuation F1) and not at all (real_word)
respectively, and the first two improved through *deterministic* work that a fine-tune would
not have produced. The third, real-word disambiguation, is a knowledge problem that a 0.6 B
model fine-tuned on GEC pairs has already been measured as unable to solve (the reranker
analysis in `RU_LANGUAGE_INTELLIGENCE_2026-08.md`: компания/кампания leans the right way by
0.02).

Before retraining could be justified, the cheaper work must be exhausted: the style layer, the
particle pack, and above all a benchmark with enough per-category items to tell a model
failure from a sampling accident. On 30 punctuation items and 46 morphology items, a fine-tune
cannot be evaluated honestly.

---

## Reproducing

```bash
dotnet build tools/langbench/langbench.csproj -c Release
tools/langbench/bin/Release/net10.0-windows/langbench.exe --layers rules,spell,lexveto,lexsignals,punct --label C
tools/langbench/bin/Release/net10.0-windows/langbench.exe --layers rules,spell,lt,lexveto,lexsignals,punct --label E
tools/morphprobe/bin/Release/net10.0-windows/morphprobe.exe --perf
tools/morphprobe/bin/Release/net10.0-windows/morphprobe.exe --check "Он живёт в новым доме."
dotnet test tests/WriteLite.Tests/WriteLite.Tests.csproj -c Release
```

Reports are in `benchmarks/results/SPRINT-*.json`.
