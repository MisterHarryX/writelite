# Morphology implementation status

`RuleBasedMorphologyService` provides local, fast heuristic morphology for the
lexical card, including lemma/part-of-speech-oriented metadata and conservative
fallbacks. It is explicitly heuristic rather than a full morphological parser.

No new full external morphology corpus or runtime has been bundled in this
release because the repository does not contain an independently verified,
redistributable source and licence. Unknown values remain unknown instead of
being reported as facts.
