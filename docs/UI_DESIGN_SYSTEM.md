# UI design system

WriteLite's desktop interface is a direct translation of
<https://writelite-web.vercel.app/ru>: warm near-black surfaces (`#080808`),
warm white foreground (`#F1EFEC`), one orange accent (`#EC6C08`) on a near-black
label, hairline borders at 4.5–12 % white, restrained 5–12 px radii, Inter for
prose and JetBrains Mono for the numbered section markers and technical values.

Every colour, size, radius, duration and control template lives in
`src/WriteLite.App/Themes/*.xaml`, merged by `WriteLiteTheme.xaml`. Views do not
style anything inline; the code-built overlay windows resolve the same tokens
through `Services/ThemeResource.cs`.

**The full specification — measured website tokens, the desktop scale derived
from them, component inventory and screen structure — is in
[DESIGN_AUDIT.md](DESIGN_AUDIT.md).**

Motion is part of the system, not decoration on top of it: durations and the
website's easing curve live in `Themes/Animations.xaml` and `Services/Motion.cs`,
press feedback is a style setter (`wl:Motion.Press`) rather than a storyboard
copied into each template, and reduced-motion is honoured. See §4.5 of the audit.

Four rules are worth repeating here because breaking them is silent:

- Cross-dictionary resource references must use `DynamicResource`. A
  `BasedOn="{StaticResource …}"` pointing at a sibling merged dictionary resolves
  during development and throws `XamlParseException` the first time the control is
  actually shown.
- An animation inside a control template cannot reach a sibling merged dictionary
  at all — `StaticResource` hits the trap above and `DynamicResource` is not
  available on a `Freezable`. Each dictionary that animates declares its own
  `<wl:EditorialEase x:Key="WlEase" />`; the curve itself is defined once, in
  `Services/EditorialEase.cs`.
- Wide uppercase mono labels are set through `controls:Type.Tracked`, not `Text`.
  WPF has no letter-spacing, and that tracking is a large part of the brand voice.
- Only `Opacity` and `RenderTransform` may be animated, so no transition can cost
  a layout pass. Hover states fade an overlay layer instead of swapping a brush,
  which also keeps every colour in `Colors.xaml`.

`DesignSystemTests` and `PageRenderSmokeTests` guard all of it, plus the palette
values, the radius range and the presence of every token the views bind to;
`DictionaryPageTests` covers the dictionary's chips, translations and history.
