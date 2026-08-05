# Performance report

The lexical service uses local packs, cancellation and a bounded cache. This
branch adds shutdown cancellation so completed popup work cannot accumulate
after a target change or application exit.

No trustworthy cold/warm-start, 15-minute RAM, p50/p95 lookup or 100-popup
measurement was collected in this automated pass. Those numbers are deliberately
omitted rather than inferred from unit-test timing.
