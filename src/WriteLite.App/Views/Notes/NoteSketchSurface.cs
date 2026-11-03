using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using WriteLite.Resources;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Views.Notes;

/// <summary>
/// A hand-drawing surface that lives inside a note, between its paragraphs.
/// </summary>
/// <remarks>
/// <para>Drawn on in place rather than in a separate window: a sketch made beside the text it
/// illustrates is the point of drawing in a note. The note's RichTextBox runs with
/// <c>IsDocumentEnabled</c>, which is what lets an embedded control take the pen.</para>
///
/// <para>The strokes travel as Ink Serialized Format inside the note's JSON. Ink is small —
/// kilobytes for a page of handwriting — and keeping it in the note means it is saved,
/// copied and undone with the text around it, with no second file to fall out of step.</para>
/// </remarks>
public sealed class NoteSketchSurface : UserControl
{
    public const double DefaultHeight = 280;
    public const double MinSurfaceHeight = 140;
    public const double MaxSurfaceHeight = 1200;

    private static readonly Color[] PenColors =
    [
        Color.FromRgb(0xEC, 0x6C, 0x08), // brand orange
        Color.FromRgb(0x4A, 0x8F, 0xD9),
        Color.FromRgb(0x5F, 0xAF, 0x7A),
        Color.FromRgb(0xE3, 0x69, 0x5A)
    ];

    private readonly InkCanvas _canvas;
    private readonly RadioButton _pen;
    private readonly RadioButton _marker;
    private readonly RadioButton _eraser;
    private Color _penColor;
    private bool _loading;

    public NoteSketchSurface(string? ink, double? height)
    {
        _canvas = new InkCanvas
        {
            Background = System.Windows.Media.Brushes.Transparent,
            Height = Math.Clamp(height ?? DefaultHeight, MinSurfaceHeight, MaxSurfaceHeight),
            Cursor = Cursors.Pen,
            ClipToBounds = true,
            UseCustomCursor = false
        };
        AutomationProperties.SetName(_canvas, Strings.NoteSketch_CanvasAutomation);

        _pen = Tool(Strings.NoteSketch_Pen);
        _marker = Tool(Strings.NoteSketch_Marker);
        _eraser = Tool(Strings.NoteSketch_Eraser);
        _pen.Click += (_, _) => SelectPen();
        _marker.Click += (_, _) => SelectMarker();
        _eraser.Click += (_, _) => SelectEraser();

        Content = BuildChrome();

        _loading = true;
        Load(ink);
        _loading = false;

        _canvas.Strokes.StrokesChanged += OnStrokesChanged;
        _canvas.StrokeCollected += (_, _) => RaiseChanged();
        _canvas.StrokeErased += (_, _) => RaiseChanged();

        Loaded += (_, _) =>
        {
            // The default pen takes the text colour of the theme it was opened in, so ink
            // drawn in a dark window is not black on near-black.
            if (_penColor == default)
            {
                _penColor = TryFindResource("WlText") is SolidColorBrush text ? text.Color : Colors.Black;
                SelectPen();
            }
        };
    }

    /// <summary>Raised whenever the drawing or its size changes, so the note can autosave.</summary>
    public event Action? Changed;

    /// <summary>Raised when the user asks to remove the whole drawing from the note.</summary>
    public event Action<NoteSketchSurface>? RemoveRequested;

    public double SurfaceHeight => _canvas.Height;

    public bool IsEmpty => _canvas.Strokes.Count == 0;

    internal InkCanvas Canvas => _canvas;

    /// <summary>The strokes as base64 ISF, or null when nothing has been drawn.</summary>
    public string? SaveInk()
    {
        if (_canvas.Strokes.Count == 0)
        {
            return null;
        }

        using var stream = new MemoryStream();
        _canvas.Strokes.Save(stream, compress: true);
        return Convert.ToBase64String(stream.ToArray());
    }

    private void Load(string? ink)
    {
        if (string.IsNullOrWhiteSpace(ink))
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(ink));
            _canvas.Strokes = new StrokeCollection(stream);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or IOException)
        {
            // A drawing that cannot be read is shown empty rather than taking the note
            // down with it; the original stays in the file until the next save.
            Services.CompatibilityLogger.Technical("note-sketch-load-failed", exception.GetType().Name);
        }
    }

    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e) => RaiseChanged();

    private void RaiseChanged()
    {
        if (!_loading)
        {
            Changed?.Invoke();
        }
    }

    // ── Tools ────────────────────────────────────────────────────────────────

    private void SelectPen()
    {
        _canvas.EditingMode = InkCanvasEditingMode.Ink;
        _canvas.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _penColor,
            Width = 2.6,
            Height = 2.6,
            FitToCurve = true,
            IgnorePressure = false
        };
        Check(_pen);
    }

    private void SelectMarker()
    {
        _canvas.EditingMode = InkCanvasEditingMode.Ink;
        _canvas.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Color.FromArgb(0xFF, 0xF2, 0xC2, 0x3A),
            Width = 14,
            Height = 14,
            IsHighlighter = true,
            FitToCurve = true,
            StylusTip = StylusTip.Rectangle
        };
        Check(_marker);
    }

    private void SelectEraser()
    {
        _canvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
        Check(_eraser);
    }

    private void Check(RadioButton active)
    {
        foreach (var tool in new[] { _pen, _marker, _eraser })
        {
            tool.IsChecked = ReferenceEquals(tool, active);
        }
    }

    // ── Chrome ───────────────────────────────────────────────────────────────

    private FrameworkElement BuildChrome()
    {
        var toolbar = new WrapPanel { Margin = new Thickness(10, 6, 6, 6), VerticalAlignment = VerticalAlignment.Center };

        var label = new TextBlock
        {
            Text = Strings.NoteSketch_Label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        label.SetResourceReference(StyleProperty, "WlMonoLabel");
        label.SetResourceReference(TextBlock.ForegroundProperty, "WlTextMuted");
        toolbar.Children.Add(label);

        toolbar.Children.Add(_pen);
        toolbar.Children.Add(_marker);
        toolbar.Children.Add(_eraser);
        toolbar.Children.Add(Rule());

        foreach (var color in PenColors)
        {
            toolbar.Children.Add(Swatch(color));
        }

        toolbar.Children.Add(Rule());

        var clear = TextButton(Strings.NoteSketch_Clear);
        clear.Click += (_, _) =>
        {
            if (_canvas.Strokes.Count > 0)
            {
                _canvas.Strokes.Clear();
            }
        };
        toolbar.Children.Add(clear);

        var remove = TextButton(Strings.NoteSketch_Remove);
        remove.Click += (_, _) => RemoveRequested?.Invoke(this);
        toolbar.Children.Add(remove);

        var grip = new Thumb
        {
            Height = 8,
            Cursor = Cursors.SizeNS,
            Background = System.Windows.Media.Brushes.Transparent,
            ToolTip = Strings.NoteSketch_ResizeTooltip
        };
        grip.Template = GripTemplate();
        grip.DragDelta += (_, e) =>
        {
            _canvas.Height = Math.Clamp(_canvas.Height + e.VerticalChange, MinSurfaceHeight, MaxSurfaceHeight);
        };
        grip.DragCompleted += (_, _) => RaiseChanged();

        var surface = new Border { Child = _canvas };
        surface.SetResourceReference(Border.BackgroundProperty, "WlBg");

        var layout = new DockPanel { LastChildFill = true };
        var header = new Border { Child = toolbar, BorderThickness = new Thickness(0, 0, 0, 1) };
        header.SetResourceReference(Border.BorderBrushProperty, "WlLine");
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(grip, Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(grip);
        layout.Children.Add(surface);

        var frame = new Border
        {
            Child = layout,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Margin = new Thickness(0, 6, 0, 10)
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "WlLine");
        frame.SetResourceReference(Border.BackgroundProperty, "WlBgSoft");
        return frame;
    }

    private static ControlTemplate GripTemplate()
    {
        var template = new ControlTemplate(typeof(Thumb));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);

        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(WidthProperty, 36.0);
        line.SetValue(HeightProperty, 3.0);
        line.SetValue(Border.CornerRadiusProperty, new CornerRadius(1.5));
        line.SetValue(HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        line.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        line.SetResourceReference(Border.BackgroundProperty, "WlLineStrong");
        border.AppendChild(line);

        template.VisualTree = border;
        return template;
    }

    private RadioButton Tool(string text)
    {
        // Tabs, not toggles: exactly one tool is active, and the tab style already reads as
        // "the current one" in this product.
        var button = new RadioButton
        {
            Content = text,
            GroupName = "sketch-" + GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture),
            Margin = new Thickness(0, 0, 14, 0),
            Focusable = false
        };
        button.SetResourceReference(StyleProperty, "WlTabButton");
        AutomationProperties.SetName(button, text);
        return button;
    }

    private Button TextButton(string text)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 4, 0), Focusable = false };
        button.SetResourceReference(StyleProperty, "WlToolbarButton");
        return button;
    }

    private static FrameworkElement Rule()
    {
        var rule = new Border();
        rule.SetResourceReference(StyleProperty, "WlToolbarRule");
        return rule;
    }

    private Button Swatch(Color color)
    {
        var dot = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(color)
        };

        var button = new Button
        {
            Content = dot,
            Width = 26,
            Height = 26,
            Margin = new Thickness(0, 0, 2, 0),
            Focusable = false,
            ToolTip = Strings.NoteSketch_ColorTooltip
        };
        button.SetResourceReference(StyleProperty, "WlIconButton");
        AutomationProperties.SetName(button, Strings.NoteSketch_ColorTooltip);
        button.Click += (_, _) =>
        {
            _penColor = color;
            SelectPen();
        };
        return button;
    }

    /// <summary>Keeps the surface out of the text box's own keyboard handling while drawing.</summary>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (e.OriginalSource is DependencyObject source && IsInside(source, _canvas))
        {
            _canvas.Focus();
        }
    }

    private static bool IsInside(DependencyObject source, DependencyObject container)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, container))
            {
                return true;
            }
        }

        return false;
    }

    internal static Brush? ThemeBrush(FrameworkElement scope, string key) => scope.TryFindResource(key) as Brush;
}
