"""Contract tests for the Russian error generator.

The generator is the only thing standing between the training corpus and
garbage: if a "corruption" is silently identical to the original, or its span
does not point at the text it claims, the model trains on noise and the failure
shows up months later as bad suggestions rather than as a crash.
"""

from __future__ import annotations

import os
import sys

import pytest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..")))

from ai.writelight_ai import ru_errors as R  # noqa: E402

SENTENCES = [
    "Я хочу купить новый ноутбук завтра утром",
    "Компания проводит исследование рынка в этом году",
    "Он сказал что придёт на встречу немного позже",
    "Разработчик написал тесты и отправил изменения в репозиторий",
    "Мы обсудим этот вопрос на следующей неделе",
    "Погода сегодня прекрасная и хочется гулять весь день",
]


@pytest.fixture(scope="module")
def generator():
    return R.RussianErrorGenerator()


def corrupt(generator, sentence, seed):
    """Returns (corrupted_text, corruptions) regardless of the result shape."""
    result = generator.corrupt(sentence, seed=seed)
    corrupted = R.apply_corrections(sentence, result) if hasattr(R, "apply_corrections") else None
    text = getattr(result, "text", None) or getattr(result, "corrupted", None)
    return text, corrupted, list(result)


def test_generator_is_deterministic(generator):
    for sentence in SENTENCES:
        first = [tuple(vars(c).items()) for c in generator.corrupt(sentence, seed=11)]
        second = [tuple(vars(c).items()) for c in generator.corrupt(sentence, seed=11)]
        assert first == second, f"same seed produced different corruptions for {sentence!r}"


def test_different_seeds_explore_the_space(generator):
    seen = set()
    for seed in range(40):
        for corruption in generator.corrupt(SENTENCES[0], seed=seed):
            seen.add(corruption.error_type)
    assert len(seen) >= 4, f"only produced {sorted(seen)} across 40 seeds"


def test_corruption_is_never_a_no_op(generator):
    for sentence in SENTENCES:
        for seed in range(30):
            for corruption in generator.corrupt(sentence, seed=seed):
                assert corruption.original != corruption.replacement, (
                    f"{corruption.error_type} produced an identical 'corruption' "
                    f"of {corruption.original!r}"
                )


def test_spans_index_the_corrupted_text(generator):
    # Spans delimit `replacement` inside the corrupted sentence: that is the
    # text the model sees, so that is where an error span has to point.
    for sentence in SENTENCES:
        for seed in range(30):
            result = generator.corrupt(sentence, seed=seed)
            for corruption in result:
                actual = result.text[corruption.start:corruption.end]
                assert actual == corruption.replacement, (
                    f"{corruption.error_type}: span {corruption.start}:{corruption.end} of "
                    f"{result.text!r} is {actual!r}, not {corruption.replacement!r}"
                )


def test_corruptions_do_not_overlap(generator):
    for sentence in SENTENCES:
        for seed in range(30):
            spans = sorted((c.start, c.end) for c in generator.corrupt(sentence, seed=seed))
            for (_, end), (start, _) in zip(spans, spans[1:]):
                assert end <= start, f"overlapping corruption spans in {sentence!r}"


def test_categories_are_computed_against_the_dictionary(generator):
    # real_word vs non_word must be decided by looking the token up, never
    # assumed from which rule produced it. Punctuation and capitalisation
    # changes are not lexical at all and are labelled as such.
    for sentence in SENTENCES:
        for seed in range(30):
            for corruption in generator.corrupt(sentence, seed=seed):
                assert corruption.category in {"real_word", "non_word", "not_lexical"}
                if corruption.category == "not_lexical":
                    continue
                # A space-insertion error splits one token into several
                # ("ноутбук" -> "ноут бук"); it only counts as a real-word error
                # when every piece is itself a word.
                tokens = corruption.replacement.split()
                expected = (
                    "real_word"
                    if tokens and all(R.is_known_word(t) for t in tokens)
                    else "non_word"
                )
                assert corruption.category == expected, (
                    f"{corruption.replacement!r} classified {corruption.category}, "
                    f"dictionary says {expected}"
                )


def test_keyboard_adjacency_is_symmetric():
    for key, neighbours in R.RU_KEY_NEIGHBOURS.items():
        for neighbour in neighbours:
            assert key in R.RU_KEY_NEIGHBOURS.get(neighbour, ()), (
                f"{key}/{neighbour} adjacency is one-way"
            )


def test_keyboard_map_covers_the_russian_alphabet():
    for letter in "йцукенгшщзхъфывапролджэячсмитьбю":
        assert R.RU_KEY_NEIGHBOURS.get(letter), f"no neighbours recorded for {letter}"


def test_layout_conversion_round_trips():
    assert R.to_ru_layout("ghbdtn") == "привет"
    assert R.to_en_layout("привет") == "ghbdtn"
    for word in ["сегодня", "программа", "ноутбук", "исследование"]:
        assert R.to_ru_layout(R.to_en_layout(word)) == word


def test_homoglyph_tables_are_inverse():
    for cyrillic, latin in R.HOMOGLYPHS_CYR_TO_LAT.items():
        assert R.HOMOGLYPHS_LAT_TO_CYR.get(latin) == cyrillic, (
            f"{cyrillic}->{latin} does not map back"
        )


def test_corruptions_are_exactly_reversible(generator):
    # The invariant the whole corpus rests on: undoing the recorded corruptions
    # must reproduce the original sentence character for character. If it does
    # not, every training pair built from it teaches the wrong correction.
    for sentence in SENTENCES:
        for seed in range(40):
            result = generator.corrupt(sentence, seed=seed)
            if not result:
                continue
            assert result.text != sentence, "corrupted text is identical to the source"
            assert R.apply_corrections(result.text, result) == sentence, (
                f"seed {seed}: undoing {[c.error_type for c in result]} on "
                f"{result.text!r} did not restore {sentence!r}"
            )
            assert result.expected_correction() == sentence


def test_error_type_registry_is_complete(generator):
    produced = set()
    for sentence in SENTENCES:
        for seed in range(200):
            for corruption in generator.corrupt(sentence, seed=seed):
                produced.add(corruption.error_type)
    unknown = produced - set(R.ERROR_TYPES)
    assert not unknown, f"generator emitted unregistered error types: {sorted(unknown)}"


def test_confusion_sets_load_and_are_usable():
    sets = R.load_confusion_sets()
    assert len(sets) >= 250, f"only {len(sets)} confusion sets"
    for entry in sets:
        assert len(entry["members"]) >= 2
        assert entry["kind"] in {"homophone", "paronym", "morphological", "orthographic"}
