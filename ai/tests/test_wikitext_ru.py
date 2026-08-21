#!/usr/bin/env python3
"""Unit tests for the ruwiktionary wikitext cleaning rules.

Fixtures are synthetic but mirror the real page structure observed on
ru.wiktionary.org, so no upstream text is reproduced here.

Run:  python -m unittest discover -s ai/tests -v
"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))

import wikitext_ru as W  # noqa: E402


class TestTemplateScanning(unittest.TestCase):
    def test_finds_balanced_top_level_templates(self):
        spans = W.find_templates("a {{x|{{y|1}}}} b {{z}}")
        self.assertEqual(len(spans), 2)
        self.assertEqual(spans[0][2], "x|{{y|1}}")
        self.assertEqual(spans[1][2], "z")

    def test_ignores_unbalanced_template(self):
        self.assertEqual(W.find_templates("a {{x|y"), [])

    def test_splits_only_on_top_level_pipes(self):
        self.assertEqual(
            W.split_params("t|{{a|b}}|[[c|d]]|e"),
            ["t", "{{a|b}}", "[[c|d]]", "e"],
        )

    def test_positional_params_skip_named(self):
        self.assertEqual(
            W.positional_params("пример|Текст|автор=Кто-то|Второй"),
            ["Текст", "Второй"],
        )


class TestStripMarkup(unittest.TestCase):
    def test_keeps_highlight_template_content(self):
        # {{выдел|...}} marks the headword inside an example and must survive.
        self.assertEqual(W.strip_markup("Просторный {{выдел|дом}}."), "Просторный дом")

    def test_drops_unknown_templates_entirely(self):
        self.assertEqual(W.strip_markup("свет {{unknown|x}} тень"), "свет тень")

    def test_resolves_piped_and_plain_links(self):
        self.assertEqual(W.strip_markup("[[крыша|крышу]] и [[дверь]]"), "крышу и дверь")

    def test_removes_quotes_refs_comments_and_tags(self):
        self.assertEqual(
            W.strip_markup("''а'' <ref>x</ref><!-- c --> <br/> б"), "а б")

    def test_preserves_yo(self):
        self.assertEqual(W.strip_markup("[[жильё]]"), "жильё")

    def test_dash_template_becomes_dash(self):
        self.assertIn("—", W.strip_markup("в кёрлинге{{-}}мишень"))

    def test_empty_sense_markers(self):
        for marker in ("# —", "# -", "# ?", "#"):
            self.assertTrue(W.is_empty_sense(marker.lstrip("# ")), marker)


class TestDefinitionParsing(unittest.TestCase):
    def test_extracts_leading_register_label(self):
        self.assertEqual(W.extract_label("# {{офиц.|ru}} совокупность корпусов"), "офиц.")

    def test_maps_perenosnoe_shorthand(self):
        self.assertEqual(W.extract_label("# {{п.|ru}} фирма"), "перен.")

    def test_combines_multiple_labels(self):
        self.assertEqual(
            W.extract_label("# {{разг.|ru}} {{шутл.|ru}} нечто"), "разг., шутл.")

    def test_no_label_when_line_starts_with_text(self):
        self.assertIsNone(W.extract_label("# место, где кто-либо проживает"))

    def test_pometa_template_used_as_label(self):
        self.assertEqual(W.extract_label("# {{помета|в [[бейсболе]]}} база"), "в бейсболе")

    def test_extracts_example_first_positional_argument(self):
        line = "# нечто {{пример|Пример {{выдел|слова}} в тексте.|Автор|Труд}}"
        self.assertEqual(W.extract_examples(line), ["Пример слова в тексте"])

    def test_ignores_empty_example_template(self):
        self.assertEqual(W.extract_examples("# нечто {{пример}}"), [])

    def test_example_limit_is_respected(self):
        line = "# x {{пример|Первый длинный пример.}} {{пример|Второй длинный пример.}}"
        self.assertEqual(len(W.extract_examples(line, limit=1)), 1)


class TestSenseLinks(unittest.TestCase):
    def test_splits_comma_separated_links(self):
        self.assertEqual(
            W.split_sense_links("# [[здание]], [[корпус]]"), ["здание", "корпус"])

    def test_empty_line_yields_nothing(self):
        self.assertEqual(W.split_sense_links("# —"), [])

    def test_deduplicates_and_limits(self):
        self.assertEqual(
            W.split_sense_links("# [[а]], [[а]], [[б]]", limit=2), ["а", "б"])

    def test_drops_overlong_phrases(self):
        self.assertEqual(W.split_sense_links("# " + " ".join(["сл"] * 6)), [])


class TestSectionSplitting(unittest.TestCase):
    PAGE = """= {{-ru-}} =

=== Морфологические и синтаксические свойства ===
{{сущ-ru|тест|м 1a}}

=== Семантические свойства ===

==== Значение ====
# первое [[значение]] {{пример|Достаточно длинный пример здесь.}}
# {{разг.|ru}} второе значение

==== Синонимы ====
# [[первый]]
# —

==== Антонимы ====
# [[противоположность]]

= {{-en-}} =

==== Значение ====
# english sense
"""

    def test_language_section_is_scoped_to_russian(self):
        russian = W.language_section(self.PAGE, "ru")
        self.assertIsNotNone(russian)
        self.assertIn("первое", russian)
        self.assertNotIn("english sense", russian)

    def test_missing_language_returns_none(self):
        self.assertIsNone(W.language_section(self.PAGE, "de"))

    def test_numbered_lines_exclude_sub_items(self):
        body = "# один\n#: пример\n#* сноска\n## вложенный\n# два\n"
        self.assertEqual(W.numbered_lines(body), ["# один", "# два"])

    def test_pos_detected_from_morphology_template(self):
        russian = W.language_section(self.PAGE, "ru")
        morph = [b for _, n, b in W.iter_sections(russian)
                 if n.startswith("Морфологические")][0]
        self.assertEqual(W.parse_pos(morph), "noun")

    def test_pos_prefixes_cover_main_classes(self):
        cases = {
            "{{сущ-ru|x|м 1a}}": "noun",
            "{{гл ru 5b-ж|основа=x}}": "verb",
            "{{прил ru 1a|основа=x}}": "adj",
            "{{нареч ru|x}}": "adv",
            "{{межд ru|x}}": "interj",
            "{{предл ru|x}}": "prep",
            "{{союз ru|x}}": "conj",
            "{{мест ru|x}}": "pron",
            "{{числ ru|x}}": "num",
            "{{част ru|x}}": "particle",
        }
        for wikitext, expected in cases.items():
            self.assertEqual(W.parse_pos(wikitext), expected, wikitext)

    def test_unknown_template_yields_no_pos(self):
        self.assertIsNone(W.parse_pos("{{неизвестно|x}}"))


if __name__ == "__main__":
    unittest.main()
