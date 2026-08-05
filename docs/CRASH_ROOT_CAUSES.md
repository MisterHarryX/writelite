# Crash root causes and fixes

## Fixed in this branch

1. **Stale lexical replacement.** The previous validity gate accepted a popup
   request when one of target or generation still happened to match and it did
   not include the text version. A delayed lookup could therefore reach a
   different text state. `LexicalRequestValidator` now requires target ID,
   generation and text version to all match.
2. **Lexical lookup outlived its view.** Changing field/text now cancels the
   outstanding lookup and hides the card before a stale result can be rendered.
3. **Shutdown races.** Pending lexical work, the double-click hook and inline
   overlay are cancelled/disposed before UI teardown. The lexical window is
   explicitly closed.
4. **Status callback race.** Language-engine status notifications now have a
   named, guarded dispatcher handler and are detached before engine disposal.

## Diagnostics policy

An automation exception is logged as operation, exception type and HRESULT.
Its message is intentionally omitted because host providers can include edited
text in exception messages. Optional feature failures are isolated and do not
need to terminate the main process.

## Remaining limits

No code-only audit can prove absence of every host-specific UIA defect. Browser
and Office UIA providers still require manual validation on the target machine.
