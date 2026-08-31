using System.Windows.Automation;
using WriteLite.Language.Core;
using WriteLite.Services;
using WriteLite.Services.Writing;

namespace WriteLite.Tests.Corrections;

/// <summary>
/// §11 and §22: verification decides, the chain falls through, and a failure is
/// non-destructive.
/// </summary>
[TestClass]
public sealed class ExternalTextWriterTests
{
    private const string Before = "Сечас программа роботает";
    private const string After = "Сечас программа работает";

    [TestMethod]
    public async Task FirstStrategyWorks_SecondIsNotTried()
    {
        var first = new FakeStrategy("first", applies: true, mutatesTo: After);
        var second = new FakeStrategy("second", applies: true, mutatesTo: After);
        var field = new FakeField(Before);
        var writer = Writer(field, first, second);

        var result = await writer.ApplyAsync(null!, Caps(), Correction(), Before);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("first", result.StrategyName);
        Assert.AreEqual(1, first.Calls);
        Assert.AreEqual(0, second.Calls);
    }

    [TestMethod]
    public async Task FirstStrategyFails_SecondIsTried()
    {
        var first = new FakeStrategy("first", applies: true, mutatesTo: null);
        var second = new FakeStrategy("second", applies: true, mutatesTo: After);
        var field = new FakeField(Before);
        var writer = Writer(field, first, second);

        var result = await writer.ApplyAsync(null!, Caps(), Correction(), Before);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("second", result.StrategyName);
        Assert.AreEqual(1, first.Calls);
        Assert.AreEqual(1, second.Calls);
    }

    [TestMethod]
    public async Task AStrategyThatClaimsSuccessWithoutChangingAnything_IsAFailure()
    {
        // §11: no exception thrown is not evidence the correction happened.
        var liar = new FakeStrategy("liar", applies: true, mutatesTo: null) { ReportSuccess = true };
        var honest = new FakeStrategy("honest", applies: true, mutatesTo: After);
        var field = new FakeField(Before);
        var writer = Writer(field, liar, honest);

        var result = await writer.ApplyAsync(null!, Caps(), Correction(), Before);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("honest", result.StrategyName);
    }

    [TestMethod]
    public async Task AllStrategiesFail_NothingIsCorrupted()
    {
        var first = new FakeStrategy("first", applies: true, mutatesTo: null);
        var second = new FakeStrategy("second", applies: true, mutatesTo: null);
        var field = new FakeField(Before);
        var writer = Writer(field, first, second);

        var result = await writer.ApplyAsync(null!, Caps(), Correction(), Before);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("exhausted", result.StrategyName);
        Assert.AreEqual(Before, field.Text);
    }

    [TestMethod]
    public async Task NoApplicableStrategy_IsReportedAsSuchRatherThanAsReadOnly()
    {
        var field = new FakeField(Before);
        var writer = Writer(field, new FakeStrategy("inapplicable", applies: false, mutatesTo: After));

        var result = await writer.ApplyAsync(null!, Caps(), Correction(), Before);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("no-applicable-strategy", result.Detail);
        Assert.IsNull(CorrectionApplicationService.StatusFor(EditabilityVerdict.Editable));
        Assert.AreEqual(
            CorrectionApplicationStatus.TargetUnavailable,
            CorrectionApplicationService.StatusFor(EditabilityVerdict.Unavailable));
        Assert.AreEqual(
            CorrectionApplicationStatus.ReadOnly,
            CorrectionApplicationService.StatusFor(EditabilityVerdict.ReadOnly));
    }

    [TestMethod]
    public async Task AThrowingStrategy_DoesNotStopTheChain()
    {
        var thrower = new ThrowingStrategy();
        var honest = new FakeStrategy("honest", applies: true, mutatesTo: After);
        var field = new FakeField(Before);
        var writer = Writer(field, thrower, honest);

        var result = await writer.ApplyAsync(null!, Caps(), Correction(), Before);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("honest", result.StrategyName);
    }

    [TestMethod]
    public void ApplyAll_ReducesToTheSpanThatActuallyChanged()
    {
        // §26 and §11: a whole-value write is neither needed nor safe when one span differs.
        var span = CorrectionApplicationService.SpanningCorrection(
            "Сечас программа роботает стабильней",
            "Сейчас программа роботает стабильней");

        // The minimal edit: «Се…час» is shared on both sides, so the write is the single
        // inserted «й» rather than the whole value.
        Assert.IsNotNull(span);
        Assert.AreEqual(2, span.Start);
        Assert.AreEqual(0, span.Length);
        Assert.AreEqual("й", span.Replacement);
        Assert.AreEqual(
            "Сейчас программа роботает стабильней",
            span.ApplyTo("Сечас программа роботает стабильней"));

        Assert.IsNull(CorrectionApplicationService.SpanningCorrection("одинаково", "одинаково"));
    }

    [TestMethod]
    public void LosingTheTrackedTarget_IsNotStaleness()
    {
        // §10: the monitor drops its target whenever focus leaves an editable field, which
        // happens routinely while a card is open — including because of WriteLite's own
        // popup. That must not be reported to the user as «Текст изменился».
        Assert.AreEqual(
            CorrectionApplicationStatus.CorrectionApplied,
            CorrectionApplicationService.ValidateSnapshotState(
                "target", 7, 12, "text", currentTargetId: null, 0, 0, currentText: null));

        // A different field, though, is a genuine move and the card no longer applies.
        Assert.AreEqual(
            CorrectionApplicationStatus.CorrectionStale,
            CorrectionApplicationService.ValidateSnapshotState(
                "target", 7, 12, "text", "another-field", 7, 12, "text"));

        // And the same field with different text is still stale.
        Assert.AreEqual(
            CorrectionApplicationStatus.CorrectionStale,
            CorrectionApplicationService.ValidateSnapshotState(
                "target", 7, 12, "text", "target", 7, 12, "changed"));
    }

    [TestMethod]
    public void RealignmentPrefersTheOccurrenceNearestTheOffsetAndRefusesATie()
    {
        // Discord's composer: Slate keeps a zero-width no-break space in the value, so an
        // offset measured through ValuePattern sits one character right of the same word in
        // the TextPattern document. One marker off, not one sentence off.
        const string document = "Сечас программа роботает";
        Assert.AreEqual(
            16,
            SelectionPasteWriteStrategy.NearestOccurrence(document, "роботает", preferred: 17));

        // Several occurrences: the one the offset points at wins, not the first in the string.
        const string twice = "роботает и снова роботает";
        Assert.AreEqual(0, SelectionPasteWriteStrategy.NearestOccurrence(twice, "роботает", 1));
        Assert.AreEqual(17, SelectionPasteWriteStrategy.NearestOccurrence(twice, "роботает", 16));

        // Equidistant occurrences are genuine ambiguity: refuse rather than guess, and let
        // the caller fail the write instead of correcting the wrong word.
        Assert.AreEqual(-1, SelectionPasteWriteStrategy.NearestOccurrence("аба", "а", preferred: 1));

        Assert.AreEqual(-1, SelectionPasteWriteStrategy.NearestOccurrence(document, "отсутствует", 0));
        Assert.AreEqual(-1, SelectionPasteWriteStrategy.NearestOccurrence(document, string.Empty, 0));
    }

    private static ExternalTextWriter Writer(FakeField field, params IExternalWriteStrategy[] strategies)
    {
        foreach (var strategy in strategies.OfType<FakeStrategy>()) strategy.Field = field;
        return new ExternalTextWriter(_ => Task.FromResult((true, field.Text)), strategies);
    }

    private static CanonicalCorrection Correction()
    {
        CanonicalCorrection.TryBind(Before, 16, 8, "роботает", "работает", out var correction);
        return correction!;
    }

    private static TextTargetCapabilities Caps() => new(
        IsEnabled: true, IsOffscreen: false, IsPassword: false, IsKeyboardFocusable: true,
        HasKeyboardFocus: true, HasValuePattern: true, ValueIsReadOnly: false, HasTextPattern: true,
        SupportsTextSelection: true, HasNativeEditWindow: false, IsTextControlType: true);

    private sealed class FakeField(string text)
    {
        public string Text { get; set; } = text;
    }

    private sealed class FakeStrategy(string name, bool applies, string? mutatesTo) : IExternalWriteStrategy
    {
        public string Name => name;
        public int Calls { get; private set; }

        /// <summary>Reports success without doing anything — the shape §11 exists to catch.</summary>
        public bool ReportSuccess { get; init; }

        public FakeField? Field { get; set; }

        public bool Applies(in TextTargetCapabilities capabilities) => applies;

        public Task<ExternalWriteResult> WriteAsync(
            AutomationElement element, CanonicalCorrection correction, CancellationToken cancellationToken)
        {
            Calls++;
            if (mutatesTo is not null && Field is not null) Field.Text = mutatesTo;
            return Task.FromResult(
                mutatesTo is not null || ReportSuccess
                    ? ExternalWriteResult.Ok(Name)
                    : ExternalWriteResult.Failed(Name, "fake-failure"));
        }
    }

    private sealed class ThrowingStrategy : IExternalWriteStrategy
    {
        public string Name => "thrower";
        public bool Applies(in TextTargetCapabilities capabilities) => true;

        public Task<ExternalWriteResult> WriteAsync(
            AutomationElement element, CanonicalCorrection correction, CancellationToken cancellationToken)
            => throw new InvalidOperationException("provider went away");
    }
}
