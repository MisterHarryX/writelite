using System.Windows;
using System.Windows.Controls;
using WriteLite.Resources;
using WriteLite.Services.Ai;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace WriteLite.Views;

/// <summary>What the user accepted from an AI action, with the result it was based on.</summary>
public sealed record AiActionOutcome(AiPreviewOutcome Outcome, TextActionResult Result);

/// <summary>
/// The AI actions menu and the preview round trip, shared by notes and the editor.
/// </summary>
/// <remarks>
/// One menu, one order, one set of names, so «Сократить» in a note and «Сократить» in a
/// document are visibly the same action. Each surface decides only what "the text" is —
/// a selection, a whole note — and how an accepted result goes back into it.
/// </remarks>
public static class AiActionRunner
{
    /// <summary>The corrector's actions, in menu order.</summary>
    public static readonly RewriteOperation[] CoreActions =
    [
        RewriteOperation.KeyPoints,
        RewriteOperation.Explain,
        RewriteOperation.Shorten,
        RewriteOperation.Expand,
        RewriteOperation.Rewrite,
        RewriteOperation.Grammar
    ];

    public static string Label(RewriteOperation operation) => operation switch
    {
        RewriteOperation.KeyPoints => Strings.AiAction_KeyPoints,
        RewriteOperation.Explain => Strings.AiAction_Explain,
        RewriteOperation.Shorten => Strings.AiAction_Shorten,
        RewriteOperation.Expand => Strings.AiAction_Expand,
        RewriteOperation.Rewrite => Strings.AiAction_Rewrite,
        RewriteOperation.Grammar => Strings.AiAction_FixErrors,
        _ => TextActionService.ActionName(operation)
    };

    /// <summary>
    /// The actions menu, headed by what it will act on so nobody has to guess whether a
    /// whole note is about to be rewritten.
    /// </summary>
    public static ContextMenu BuildMenu(FrameworkElement owner, string scope, Action<RewriteOperation> run)
    {
        var menu = new ContextMenu { Style = owner.TryFindResource("WlContextMenu") as Style };

        menu.Items.Add(new MenuItem
        {
            Header = scope,
            IsEnabled = false,
            Style = owner.TryFindResource("WlMenuItem") as Style
        });
        menu.Items.Add(new Separator { Style = owner.TryFindResource("WlMenuSeparator") as Style });

        foreach (var operation in CoreActions)
        {
            if (operation == RewriteOperation.Shorten || operation == RewriteOperation.Grammar)
            {
                menu.Items.Add(new Separator { Style = owner.TryFindResource("WlMenuSeparator") as Style });
            }

            var item = new MenuItem
            {
                Header = Label(operation),
                Style = owner.TryFindResource("WlMenuItem") as Style
            };
            var captured = operation;
            item.Click += (_, _) => run(captured);
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>
    /// Runs an action behind the preview window and returns what the user accepted, or null.
    /// </summary>
    /// <remarks>
    /// The service runs on the thread pool: the checking stack's deterministic pass is
    /// synchronous work, and the preview's busy line and cancel button must stay alive
    /// while it runs.
    /// </remarks>
    public static AiActionOutcome? Run(Window? owner, TextActionService service, TextActionRequest request)
    {
        TextActionResult? last = null;

        var preview = new AiPreviewWindow(
            Label(request.Operation),
            async (progress, token) =>
            {
                var result = await Task.Run(() => service.RunAsync(request, progress, token), token);
                last = result;
                return result.ToRewriteResult();
            })
        {
            Owner = owner
        };

        var accepted = preview.ShowDialog() == true;
        return accepted && last is not null && preview.Outcome != AiPreviewOutcome.Cancelled
            ? new AiActionOutcome(preview.Outcome, last)
            : null;
    }
}
