using System.Windows;
using System.Windows.Documents;
using WriteLite.Services.Documents;
using WriteLite.Services.Personal;
using WriteLite.Services.Settings;
using WriteLite.Views.Pages;
using WpfParagraph = System.Windows.Documents.Paragraph;

namespace WriteLite.Tests.Personal;

/// <summary>Word prediction as the editor shows it: painted as ghost text, accepted with Tab.</summary>
[TestClass]
public sealed class EditorPredictionTests
{
    private static (EditorPage Page, Window Window, string Root) Host(PersonalStyleService style)
    {
        WpfTestHost.EnsureThemeApplied();

        var root = Path.Combine(Path.GetTempPath(), "wl-predict-" + Guid.NewGuid().ToString("N"));
        var page = new EditorPage
        {
            Recovery = new DocumentRecoveryService(Path.Combine(root, "recovery")),
        };
        page.ReportError = (_, _) => { };
        page.AskConfirmation = (_, _) => true;
        page.BindPersonalStyle(style);

        var window = new Window
        {
            Width = 1280,
            Height = 800,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            Content = page,
        };
        window.Show();
        window.UpdateLayout();
        return (page, window, root);
    }

    private static void TypeAtEnd(EditorPage page, string text)
    {
        page.Editor.Document = new FlowDocument(new WpfParagraph(new Run(text)));
        page.Editor.CaretPosition = page.Editor.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        page.Editor.UpdateLayout();
    }

    [TestMethod]
    public void The_predicted_word_is_painted_and_Tab_inserts_it() => WpfTestHost.RunAsync(async () =>
    {
        using var style = PersonalStyleService.CreateInMemory();
        style.LearnNow("Спасибо за подробный ответ. Подробный план пришлю вечером.");

        var (page, window, root) = Host(style);
        try
        {
            TypeAtEnd(page, "Очень подр");
            await WpfTestHost.PumpAsync(80);

            page.UpdatePersonalPrediction();

            Assert.IsNotNull(page.CurrentPrediction);
            Assert.AreEqual("подробный", page.CurrentPrediction.Word);
            Assert.IsTrue(page.HasWritingSuggestion, "the prediction must be on screen for Tab to accept it");

            Assert.IsTrue(page.AcceptWritingSuggestion());
            await WpfTestHost.PumpAsync(80);

            var text = new TextRange(page.Editor.Document.ContentStart, page.Editor.Document.ContentEnd).Text.TrimEnd();
            Assert.AreEqual("Очень подробный", text);
            Assert.AreEqual(1, style.Snapshot().PredictionsAccepted);
        }
        finally
        {
            window.Close();
            page.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    });

    [TestMethod]
    public void Nothing_is_painted_when_prediction_is_switched_off() => WpfTestHost.RunAsync(async () =>
    {
        using var style = PersonalStyleService.CreateInMemory();
        style.LearnNow("Спасибо за подробный ответ. Подробный план пришлю вечером.");
        style.ApplySettings(new WriteLiteAppSettings { PersonalWordPrediction = false });

        var (page, window, root) = Host(style);
        try
        {
            TypeAtEnd(page, "Очень подр");
            await WpfTestHost.PumpAsync(80);

            page.UpdatePersonalPrediction();

            Assert.IsNull(page.CurrentPrediction);
            Assert.IsFalse(page.HasWritingSuggestion);
        }
        finally
        {
            window.Close();
            page.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    });
}
