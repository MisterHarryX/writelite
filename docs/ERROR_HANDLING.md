# Safe error handling

WriteLite uses `CompatibilityLogger` for bounded rotating technical logs. The
active log rotates at 2 MiB and three archives are retained. Safe error records
contain an event/operation, process metadata where available, exception type
and HRESULT. They do not store the exception message, stack dump, field text,
selected word, clipboard contents, URL, email or path.

`App` observes dispatcher, task-scheduler and application-domain faults so an
optional analysis, lexical lookup or popup operation can fail independently.
The language-engine UI status callback is dispatcher-safe during shutdown.

The diagnostics page exposes existing local diagnostics, log-folder access and
technical-copy actions. Cache reset, AI restart and dictionary validation must
only be advertised when their backing operation is present; this release does
not claim a global system repair capability.
