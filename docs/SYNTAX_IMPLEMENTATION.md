# Syntax implementation status

`SyntacticRoleAnalyzer` supplies a local best-effort role estimate used by the
lexical card. It is not a dependency parser and must be interpreted as an
approximation. Low-confidence results are not promoted to a definitive grammar
fact.

A production dependency parser for Russian and English needs a separately
licensed model and a reproducible on-device runtime; neither was added without
that provenance.
