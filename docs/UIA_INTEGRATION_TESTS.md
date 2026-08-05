# UI Automation integration tests

The solution contains deterministic unit coverage for editable-target policy,
underline geometry, popup placement, range replacement, lexical requests and
protected tokens. It does not yet contain the requested separate WPF/WinForms
test-host project or unattended cross-process UIA suite.

Manual host testing is still required for Notepad, Edge, Chrome, Word (when
installed), RichTextBox, password and read-only controls, DPI changes and
multiple monitors. Tests must report a host as supported only after it passes
an actual release-build scenario.
