using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Automation;
using WriteLite.Language.Core;
using WriteLite.Services;
using WriteLite.Services.Writing;

namespace FieldMatrix;

/// <summary>
/// Measures WriteLite's external-field compatibility against real controls, using the
/// shipping capability layer and the shipping write strategies.
/// </summary>
/// <remarks>
/// <para>§13 and §15 ask for a compatibility matrix that reports what was actually observed
/// rather than what the architecture ought to allow. This drives the production code — the
/// same <see cref="TextTargetCapabilities"/>, the same
/// <see cref="TextTargetCapabilityPolicy"/>, the same <see cref="ExternalTextWriter"/> — so
/// a row is evidence about WriteLite and not about a test double.</para>
///
/// <para><c>--matrix</c> opens each target in turn and prints a row. <c>--focused</c> probes
/// whatever the user focuses during a countdown, for applications that need a human to reach
/// the right field.</para>
/// </remarks>
internal static class Program
{
    private const string Marker = "роботает";
    private const string Fixed = "работает";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Contains("--e2e")) return EndToEnd.Run(args);
        return args.Contains("--focused")
            ? RunFocused(args)
            : RunMatrix(args);
    }

    private static int RunFocused(string[] args)
    {
        var label = Arg(args, "--label") ?? "focused control";
        var countdown = int.TryParse(Arg(args, "--wait"), out var seconds) ? seconds : 8;

        Console.WriteLine($"Focus the field under test — probing in {countdown}s ({label})");
        for (var i = countdown; i > 0; i--)
        {
            Console.Write($"{i}… ");
            Thread.Sleep(1000);
        }

        Console.WriteLine();
        AutomationElement element;
        try { element = AutomationElement.FocusedElement; }
        catch (Exception exception)
        {
            Console.WriteLine($"FAIL  no focused element: {exception.GetType().Name}");
            return 2;
        }

        if (args.Contains("--fill") && !Targets.FillFocused(element))
        {
            Console.WriteLine("FAIL  could not put the probe sentence in the field");
            return 2;
        }

        var row = Probe(label, Arg(args, "--tech") ?? "manual", element).GetAwaiter().GetResult();
        Console.WriteLine();
        Console.WriteLine(MatrixHeader());
        Console.WriteLine(row.ToMarkdown());
        return row.Replace == "PASS" || row.Replace == "n/a" ? 0 : 1;
    }

    private static int RunMatrix(string[] args)
    {
        var only = Arg(args, "--only");
        var root = RepoRoot();
        var rows = new List<MatrixRow>();

        var targets = Targets.All(root).ToList();
        if (args.Contains("--include-open-apps"))
        {
            // Opt-in: these type into applications the user already has open. See
            // Targets.OpenApplications for why they are not in the default run.
            targets.AddRange(Targets.OpenApplications());
        }

        foreach (var target in targets)
        {
            if (only is not null && !target.Name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;

            Console.WriteLine();
            Console.WriteLine(new string('─', 78));
            Console.WriteLine($"▶ {target.Name}  ({target.Technology})");

            ProbeSession? session = null;
            try { session = target.Open(); }
            catch (Exception exception)
            {
                Console.WriteLine($"  open failed: {exception.GetType().Name} {exception.Message}");
            }

            if (session is null)
            {
                rows.Add(MatrixRow.Unavailable(target.Name, target.Technology));
                continue;
            }

            rows.Add(Probe(target.Name, target.Technology, session.Element).GetAwaiter().GetResult());
        }

        Console.WriteLine();
        Console.WriteLine(new string('═', 78));
        Console.WriteLine(MatrixHeader());
        foreach (var row in rows) Console.WriteLine(row.ToMarkdown());

        var outPath = Arg(args, "--out");
        if (outPath is not null)
        {
            var builder = new StringBuilder();
            builder.AppendLine(MatrixHeader());
            foreach (var row in rows) builder.AppendLine(row.ToMarkdown());
            File.WriteAllText(outPath, builder.ToString(), Encoding.UTF8);
            Console.WriteLine($"\nwritten: {outPath}");
        }

        return rows.Any(r => r.Replace == "FAIL") ? 1 : 0;
    }

    private static async Task<MatrixRow> Probe(string label, string technology, AutomationElement element)
    {
        var capabilities = SafeCapabilities(element);
        var verdict = TextTargetCapabilityPolicy.Evaluate(capabilities);
        var discoverable = TextTargetCapabilityPolicy.IsDiscoverableTarget(capabilities);

        var adapter = new CompositeTextTargetAdapter();
        var selected = adapter.Select(element);
        var read = selected is null
            ? (Succeeded: false, Text: string.Empty)
            : await selected.ReadTextAsync(element);

        Console.WriteLine($"  process        : {TryProcessName(element)}");
        Console.WriteLine($"  controlType    : {Safe(() => element.Current.LocalizedControlType)}"
                          + $"   class={Safe(() => element.Current.ClassName)}");
        Console.WriteLine($"  capabilities   : value={Bit(capabilities.HasValuePattern)}"
                          + $" valueRO={Bit(capabilities.ValueIsReadOnly)}"
                          + $" text={Bit(capabilities.HasTextPattern)}"
                          + $" select={Bit(capabilities.SupportsTextSelection)}"
                          + $" hwnd={Bit(capabilities.HasNativeEditWindow)}"
                          + $" focusable={Bit(capabilities.IsKeyboardFocusable)}"
                          + $" textType={Bit(capabilities.IsTextControlType)}"
                          + $" password={Bit(capabilities.IsPassword)}");
        Console.WriteLine($"  verdict        : {verdict}   discoverable={discoverable}");
        Console.WriteLine($"  read           : {(read.Succeeded ? "PASS" : "FAIL")}"
                          + $" adapter={selected?.Name ?? "none"} length={read.Text.Length}");

        var writer = new ExternalTextWriter(_ => ReadBack(adapter, element));
        var plan = writer.PlanFor(capabilities);
        var planText = plan.Count == 0 ? "(none)" : string.Join(" → ", plan);
        Console.WriteLine($"  strategy plan  : {planText}");

        var row = new MatrixRow(label, technology)
        {
            Read = read.Succeeded ? "PASS" : "FAIL",
            Detect = verdict.ToString(),
            Plan = planText,
        };

        if (!read.Succeeded)
        {
            Console.WriteLine("  replace        : SKIP (nothing readable)");
            row.Replace = "FAIL";
            row.Verified = "FAIL";
            return row;
        }

        Console.WriteLine($"  text           : «{Preview(read.Text)}»");

        if (verdict != EditabilityVerdict.Editable)
        {
            // A truly read-only control is a pass when WriteLite refuses to touch it.
            Console.WriteLine($"  replace        : NOT ATTEMPTED (verdict={verdict})");
            row.Replace = "n/a";
            row.Verified = "n/a";
            return row;
        }

        var index = read.Text.IndexOf(Marker, StringComparison.Ordinal);
        if (index < 0)
        {
            Console.WriteLine($"  replace        : SKIP («{Marker}» not present in the field)");
            row.Replace = "SKIP";
            row.Verified = "SKIP";
            return row;
        }

        if (CanonicalCorrection.TryBind(read.Text, index, Marker.Length, Marker, Fixed, out var correction)
            != CorrectionBindingStatus.Bound || correction is null)
        {
            Console.WriteLine("  replace        : FAIL (correction would not bind)");
            row.Replace = "FAIL";
            row.Verified = "FAIL";
            return row;
        }

        if (Environment.GetEnvironmentVariable("FIELDMATRIX_DIAGNOSE") == "1")
        {
            RangeDiagnostic.Run(element, Marker);
        }

        var expected = correction.ApplyTo(read.Text);
        var stopwatch = Stopwatch.StartNew();
        var result = await writer.ApplyAsync(element, capabilities, correction, read.Text);
        stopwatch.Stop();

        var after = await ReadBack(adapter, element);
        var intact = string.Equals(after.Text, expected, StringComparison.Ordinal);

        Console.WriteLine($"  replace        : {(result.Succeeded ? "PASS" : "FAIL")}"
                          + $" strategy={result.StrategyName} detail={result.Detail}"
                          + $" ms={stopwatch.ElapsedMilliseconds}");
        Console.WriteLine($"  verified       : {(intact ? "PASS" : "FAIL")}");
        Console.WriteLine($"  after          : «{Preview(after.Text)}»");

        row.Replace = result.Succeeded ? "PASS" : "FAIL";
        row.Verified = intact ? "PASS" : "FAIL";
        row.Strategy = result.StrategyName;
        row.LatencyMs = stopwatch.ElapsedMilliseconds;
        return row;
    }

    private static TextTargetCapabilities SafeCapabilities(AutomationElement element)
    {
        try { return TextTargetCapabilities.Read(element); }
        catch (Exception exception)
        {
            Console.WriteLine($"  capabilities failed: {exception.GetType().Name}");
            return TextTargetCapabilities.Unknown;
        }
    }

    private static async Task<(bool Succeeded, string Text)> ReadBack(
        CompositeTextTargetAdapter adapter, AutomationElement element)
    {
        try
        {
            var selected = adapter.Select(element);
            return selected is null ? (false, string.Empty) : await selected.ReadTextAsync(element);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    private static string MatrixHeader() =>
        "| Application / control | Technology | Read | Detect | Replace | Verified | Strategy | ms |\n"
        + "| --- | --- | ---: | ---: | ---: | ---: | --- | ---: |";

    private static string Bit(bool value) => value ? "1" : "0";

    private static string Preview(string text)
    {
        var oneLine = text.Replace("\r", "\\r").Replace("\n", "\\n");
        return oneLine.Length <= 96 ? oneLine : oneLine[..96] + "…";
    }

    private static string Safe(Func<string?> read)
    {
        try { return read() ?? "?"; }
        catch { return "?"; }
    }

    private static string TryProcessName(AutomationElement element)
    {
        try { return Process.GetProcessById(element.Current.ProcessId).ProcessName; }
        catch { return "?"; }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WriteLite.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    internal static string? Arg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

internal sealed class MatrixRow(string name, string technology)
{
    public string Read { get; set; } = "—";
    public string Detect { get; set; } = "—";
    public string Replace { get; set; } = "—";
    public string Verified { get; set; } = "—";
    public string Strategy { get; set; } = "—";
    public string Plan { get; set; } = "—";
    public long LatencyMs { get; set; }

    public static MatrixRow Unavailable(string name, string technology)
        => new(name, technology) { Read = "n/a", Detect = "not available", Replace = "n/a", Verified = "n/a" };

    public string ToMarkdown()
        => $"| {name} | {technology} | {Read} | {Detect} | {Replace} | {Verified} | {Strategy} | "
           + $"{(LatencyMs > 0 ? LatencyMs.ToString() : "—")} |";
}
