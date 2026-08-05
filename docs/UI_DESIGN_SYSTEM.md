# UI design system

WriteLite uses the dark orange product palette: near-black backgrounds,
elevated charcoal cards, soft borders, orange action accents and muted neutral
text. Correction and lexical popups are borderless reusable WPF windows with
rounded cards, DPI-aware smart placement and keyboard Escape handling.

The present codebase still contains some legacy code-built brushes. A complete
move to the requested `Themes/*.xaml` token files is not claimed by this
stability release; visual migration must be validated screen-by-screen so it
does not regress host overlays.
