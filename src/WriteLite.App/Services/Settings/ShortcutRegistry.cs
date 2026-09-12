using System.Text;
using System.Windows.Input;
using WriteLite.Resources;

namespace WriteLite.Services.Settings;

/// <summary>Where a shortcut is heard.</summary>
public enum ShortcutScope
{
    /// <summary>Anywhere in Windows, including while another application has focus.</summary>
    Global,

    /// <summary>Only inside WriteLite's own windows.</summary>
    Application
}

/// <summary>One shortcut the product declares, independent of what it is currently bound to.</summary>
/// <param name="Id">Stable identifier. Persisted, so it must not change once shipped.</param>
/// <param name="Name">What the shortcut does, as the settings page names it.</param>
/// <param name="Section">Which group it appears under.</param>
/// <param name="DefaultGesture">The binding WriteLite ships with.</param>
/// <param name="IsRebindable">
/// False for the few keys whose meaning is not WriteLite's to redefine — see
/// <see cref="ShortcutRegistry"/>.
/// </param>
public sealed record ShortcutDefinition(
    string Id,
    string Name,
    string Section,
    ShortcutScope Scope,
    string DefaultGesture,
    bool IsRebindable = true);

/// <summary>Why a proposed binding was refused. <see cref="None"/> means it was taken.</summary>
public enum RebindRefusal
{
    None,
    UnknownShortcut,
    NotRebindable,
    NoKey,
    ModifierRequired,
    ReservedKey,
    Conflict
}

/// <summary>The outcome of trying to bind a key to a shortcut.</summary>
/// <param name="ConflictsWith">
/// When <see cref="RebindRefusal.Conflict"/>, the name of the shortcut already holding it —
/// so the message can say which, rather than only that something does.
/// </param>
public readonly record struct RebindResult(RebindRefusal Refusal, string? ConflictsWith = null)
{
    public bool Succeeded => Refusal == RebindRefusal.None;

    public static RebindResult Ok => new(RebindRefusal.None);

    public string Message => Refusal switch
    {
        RebindRefusal.None => string.Empty,
        RebindRefusal.UnknownShortcut => Strings.Shortcut_ErrorUnknown,
        RebindRefusal.NotRebindable => Strings.Shortcut_ErrorNotRebindable,
        RebindRefusal.NoKey => Strings.Shortcut_ErrorNoKey,
        RebindRefusal.ModifierRequired => Strings.Shortcut_ErrorModifierRequired,
        RebindRefusal.ReservedKey => Strings.Shortcut_ErrorReservedKey,
        RebindRefusal.Conflict => ConflictsWith is null
            ? Strings.Shortcut_ErrorConflict
            : string.Format(Strings.Shortcut_ErrorConflictWith, ConflictsWith),
        _ => Strings.Shortcut_ErrorFailed
    };
}

/// <summary>
/// Every keyboard shortcut in WriteLite, in one place, with what it is bound to now.
/// </summary>
/// <remarks>
/// <para><b>Why a registry.</b> The shortcuts were nine <c>switch</c> statements in six files.
/// Nothing could list them, so the product could not show the user what it responded to;
/// nothing could compare them, so two of them binding the same keys would simply mean the
/// first one won, silently, depending on which handler ran first. A registry makes both
/// questions answerable: what exists, and whether a proposed binding collides with something
/// that already exists.</para>
///
/// <para><b>What may be rebound.</b> Almost everything. The exceptions are keys whose meaning
/// belongs to Windows and to every application in it rather than to WriteLite — Escape
/// dismisses, Tab accepts the thing under the caret — and rebinding those would be WriteLite
/// deciding that the conventions of the desktop are a preference. They are listed in the
/// settings page anyway, marked fixed, because "what does this application do when I press
/// Escape" is a question the page exists to answer.</para>
///
/// <para><b>What counts as a valid binding.</b> A shortcut has to be reachable and must not
/// eat ordinary typing. A bare letter is refused because it would; a bare function key is
/// accepted because it would not. The Windows key is refused outright: the shell claims
/// combinations with it at a level an application cannot see, so binding one would produce a
/// shortcut that silently never fires.</para>
///
/// <para>Only the differences from the defaults are stored. A settings file therefore says
/// what the reader changed rather than restating the shipped set, and a future release that
/// changes a default reaches everyone who never touched it.</para>
/// </remarks>
public sealed class ShortcutRegistry
{
    // ── Identifiers ──────────────────────────────────────────────────────────
    public const string OpenWriteLite = "app.open";
    public const string NewDocument = "editor.new";
    public const string OpenDocument = "editor.open";
    public const string SaveDocument = "editor.save";
    public const string SaveDocumentAs = "editor.save-as";
    public const string Bold = "editor.bold";
    public const string Italic = "editor.italic";
    public const string Underline = "editor.underline";
    public const string Find = "editor.find";
    public const string Replace = "editor.replace";
    public const string CheckNow = "editor.check-now";
    public const string AcceptSuggestion = "editor.accept-suggestion";
    public const string PreviousPage = "reader.previous-page";
    public const string NextPage = "reader.next-page";
    public const string FullScreen = "shell.fullscreen";
    public const string Dismiss = "shell.dismiss";

    private static readonly string SectionGlobal = Strings.Shortcut_SectionGlobal;
    private static readonly string SectionEditor = Strings.Nav_Editor;
    private static readonly string SectionReader = Strings.Nav_Reading;
    private static readonly string SectionShell = Strings.Shortcut_SectionWindow;

    /// <summary>Every shortcut the product has, in the order the settings page shows them.</summary>
    public static IReadOnlyList<ShortcutDefinition> Definitions { get; } =
    [
        new(OpenWriteLite, Strings.Shortcut_OpenWriteLite, SectionGlobal, ShortcutScope.Global, "Ctrl+Alt+W"),

        new(CheckNow, Strings.Shortcut_CheckNow, SectionEditor, ShortcutScope.Application, "Ctrl+Enter"),
        new(NewDocument, Strings.Shortcut_NewDocument, SectionEditor, ShortcutScope.Application, "Ctrl+N"),
        new(OpenDocument, Strings.EditorDoc_OpenTitle, SectionEditor, ShortcutScope.Application, "Ctrl+O"),
        new(SaveDocument, Strings.Menu_Save, SectionEditor, ShortcutScope.Application, "Ctrl+S"),
        new(SaveDocumentAs, Strings.Menu_SaveAs, SectionEditor, ShortcutScope.Application, "Ctrl+Shift+S"),
        new(Bold, Strings.Editor_Bold, SectionEditor, ShortcutScope.Application, "Ctrl+B"),
        new(Italic, Strings.Editor_Italic, SectionEditor, ShortcutScope.Application, "Ctrl+I"),
        new(Underline, Strings.Editor_Underline, SectionEditor, ShortcutScope.Application, "Ctrl+U"),
        new(Find, Strings.Editor_FindBox, SectionEditor, ShortcutScope.Application, "Ctrl+F"),
        new(Replace, Strings.Editor_FindReplace, SectionEditor, ShortcutScope.Application, "Ctrl+H"),
        new(AcceptSuggestion, Strings.Shortcut_AcceptSuggestion, SectionEditor, ShortcutScope.Application, "Tab", IsRebindable: false),

        new(PreviousPage, Strings.Shortcut_PreviousPage, SectionReader, ShortcutScope.Application, "Ctrl+Left"),
        new(NextPage, Strings.Shortcut_NextPage, SectionReader, ShortcutScope.Application, "Ctrl+Right"),

        new(FullScreen, Strings.Shortcut_FullScreen, SectionShell, ShortcutScope.Application, "F11"),
        new(Dismiss, Strings.Shortcut_Dismiss, SectionShell, ShortcutScope.Application, "Escape", IsRebindable: false),
    ];

    private static readonly Dictionary<string, ShortcutDefinition> ById =
        Definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

    private readonly Dictionary<string, string> _overrides;

    public ShortcutRegistry(IReadOnlyDictionary<string, string>? overrides = null)
    {
        _overrides = new Dictionary<string, string>(StringComparer.Ordinal);

        if (overrides is null) return;

        foreach (var (id, gesture) in overrides)
        {
            // A settings file naming a shortcut this build does not have, or a gesture it
            // cannot parse, is dropped rather than allowed to fail the load. The user gets
            // the default for that one shortcut and everything else they configured.
            if (!ById.ContainsKey(id)) continue;
            if (ShortcutGesture.TryParse(gesture, out _, out _)) _overrides[id] = gesture;
        }
    }

    /// <summary>Bindings that differ from the shipped defaults. This is what gets persisted.</summary>
    public IReadOnlyDictionary<string, string> Overrides => _overrides;

    /// <summary>The gesture a shortcut is bound to now, as text.</summary>
    public string GestureOf(string id)
    {
        if (_overrides.TryGetValue(id, out var custom)) return custom;
        return ById.TryGetValue(id, out var definition) ? definition.DefaultGesture : string.Empty;
    }

    /// <summary>True when this shortcut has been changed from what WriteLite ships with.</summary>
    public bool IsCustomised(string id) => _overrides.ContainsKey(id);

    /// <summary>Which shortcut a keypress means, or null when it means nothing.</summary>
    public string? Match(Key key, ModifierKeys modifiers, ShortcutScope scope)
    {
        foreach (var definition in Definitions)
        {
            if (definition.Scope != scope) continue;
            if (!ShortcutGesture.TryParse(GestureOf(definition.Id), out var boundKey, out var boundModifiers))
            {
                continue;
            }

            if (boundKey == key && boundModifiers == modifiers) return definition.Id;
        }

        return null;
    }

    /// <summary>
    /// Binds a shortcut to a keypress, or explains why it cannot be.
    /// </summary>
    public RebindResult TryRebind(string id, Key key, ModifierKeys modifiers)
    {
        if (!ById.TryGetValue(id, out var definition)) return new RebindResult(RebindRefusal.UnknownShortcut);
        if (!definition.IsRebindable) return new RebindResult(RebindRefusal.NotRebindable);

        var validation = ShortcutGesture.Validate(key, modifiers, definition.Scope);
        if (validation != RebindRefusal.None) return new RebindResult(validation);

        foreach (var other in Definitions)
        {
            if (string.Equals(other.Id, id, StringComparison.Ordinal)) continue;
            if (other.Scope != definition.Scope) continue;
            if (!ShortcutGesture.TryParse(GestureOf(other.Id), out var otherKey, out var otherModifiers)) continue;

            if (otherKey == key && otherModifiers == modifiers)
            {
                return new RebindResult(RebindRefusal.Conflict, other.Name);
            }
        }

        var gesture = ShortcutGesture.Format(key, modifiers);
        if (string.Equals(gesture, definition.DefaultGesture, StringComparison.Ordinal))
        {
            // Back to the shipped binding: stop recording an override, so a future release
            // that changes this default reaches this user too.
            _overrides.Remove(id);
        }
        else
        {
            _overrides[id] = gesture;
        }

        return RebindResult.Ok;
    }

    /// <summary>Puts one shortcut back to what WriteLite ships with.</summary>
    public void ResetToDefault(string id) => _overrides.Remove(id);

    /// <summary>Puts every shortcut back to what WriteLite ships with.</summary>
    public void ResetAll() => _overrides.Clear();
}

/// <summary>Turning a keypress into text a person can read, and back again.</summary>
/// <remarks>
/// Its own type rather than methods on the registry, because parsing and formatting are what
/// the settings page needs on their own — to show a binding while the user is still holding
/// the keys down, before anything has been bound.
/// </remarks>
public static class ShortcutGesture
{
    /// <summary>Renders a keypress the way the settings page and the menus show it.</summary>
    public static string Format(Key key, ModifierKeys modifiers)
    {
        if (key == Key.None) return string.Empty;

        var text = new StringBuilder();
        if (modifiers.HasFlag(ModifierKeys.Control)) text.Append("Ctrl+");
        if (modifiers.HasFlag(ModifierKeys.Alt)) text.Append("Alt+");
        if (modifiers.HasFlag(ModifierKeys.Shift)) text.Append("Shift+");
        text.Append(NameOf(key));
        return text.ToString();
    }

    /// <summary>Reads a rendered gesture back. False when the text names nothing usable.</summary>
    public static bool TryParse(string? gesture, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;

        if (string.IsNullOrWhiteSpace(gesture)) return false;

        foreach (var part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModifierKeys.Control;
                    continue;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    continue;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    continue;
                case "win" or "windows":
                    modifiers |= ModifierKeys.Windows;
                    continue;
            }

            if (key != Key.None) return false;   // two keys is not a gesture
            if (!TryParseKey(part, out key)) return false;
        }

        return key != Key.None;
    }

    /// <summary>Whether a keypress may be bound at all, before anything about conflicts.</summary>
    public static RebindRefusal Validate(Key key, ModifierKeys modifiers, ShortcutScope scope)
    {
        if (key is Key.None
            or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.System)
        {
            // A modifier on its own is not a shortcut, and Key.System is what WPF reports for
            // the key held with Alt rather than a key in its own right.
            return RebindRefusal.NoKey;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            // The shell claims these before any application sees them, so accepting one
            // would produce a shortcut that never fires and no way to find out why.
            return RebindRefusal.ReservedKey;
        }

        if (scope == ShortcutScope.Global)
        {
            // Shift alone leaves a capital letter as a system-wide shortcut. It would fire
            // every time anyone typed one.
            if (!modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Alt))
            {
                return RebindRefusal.ModifierRequired;
            }

            return RebindRefusal.None;
        }

        if (modifiers != ModifierKeys.None) return RebindRefusal.None;

        // No modifier. Only keys that are not ordinary typing may stand alone: binding a bare
        // letter would make it impossible to type that letter anywhere in the application.
        return IsStandaloneKey(key) ? RebindRefusal.None : RebindRefusal.ModifierRequired;
    }

    private static bool IsStandaloneKey(Key key) =>
        key is >= Key.F1 and <= Key.F24
            or Key.Escape
            or Key.Insert
            or Key.Delete
            or Key.Home
            or Key.End
            or Key.PageUp
            or Key.PageDown
            or Key.Tab;

    private static string NameOf(Key key) => key switch
    {
        Key.Return => "Enter",
        Key.Escape => "Escape",
        Key.Prior => "PageUp",
        Key.Next => "PageDown",
        Key.Space => "Space",
        Key.Back => "Backspace",
        Key.OemPlus => "+",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        _ => key.ToString()
    };

    private static bool TryParseKey(string name, out Key key)
    {
        switch (name.ToLowerInvariant())
        {
            case "enter": key = Key.Return; return true;
            case "pageup": key = Key.Prior; return true;
            case "pagedown": key = Key.Next; return true;
            case "backspace": key = Key.Back; return true;
            case "+": key = Key.OemPlus; return true;
            case "-": key = Key.OemMinus; return true;
            case ",": key = Key.OemComma; return true;
            case ".": key = Key.OemPeriod; return true;
        }

        if (name.Length == 1 && name[0] is >= '0' and <= '9')
        {
            key = Key.D0 + (name[0] - '0');
            return true;
        }

        return Enum.TryParse(name, ignoreCase: true, out key) && key != Key.None;
    }
}
