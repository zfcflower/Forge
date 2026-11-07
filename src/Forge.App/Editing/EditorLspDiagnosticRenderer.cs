using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Forge.Editor.Lsp;

namespace Forge.App.Editing;

/// <summary>Draws diagnostics only for visible lines; document segments follow edits.</summary>
internal sealed class EditorLspDiagnosticRenderer : IBackgroundRenderer, IDisposable
{
    private const int MaxSegmentsPerFrame = 400;
    private readonly TextDocument document;
    private readonly TextSegmentCollection<Marker> markers;
    private readonly Pen?[] pens = new Pen?[5];

    public EditorLspDiagnosticRenderer(TextDocument document)
    {
        this.document = document;
        markers = new TextSegmentCollection<Marker>(document);
    }

    public KnownLayer Layer => KnownLayer.Selection;

    public void SetDiagnostics(EditorLspDiagnosticSet set)
    {
        markers.Clear();
        foreach (var item in set.Items)
        {
            if (item.Start >= 0 && item.End > item.Start && item.End <= document.TextLength)
            {
                markers.Add(new Marker(item));
            }
        }
    }

    public void Clear() => markers.Clear();

    public IReadOnlyList<EditorLspDiagnostic> FindAt(int offset)
    {
        var found = markers.FindSegmentsContaining(offset);
        if (found.Count == 0)
        {
            return [];
        }

        return [.. found
            .Select(static marker => marker.Diagnostic with { Start = marker.StartOffset, End = marker.EndOffset })
            .OrderBy(static diagnostic => (int)diagnostic.Severity)];
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (markers.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0)
        {
            return;
        }

        var lines = textView.VisualLines;
        var viewStart = lines[0].FirstDocumentLine.Offset;
        var viewEnd = lines[^1].LastDocumentLine.EndOffset;
        Array.Clear(pens);
        var drawn = 0;
        foreach (var marker in markers.FindOverlappingSegments(viewStart, viewEnd - viewStart))
        {
            var pen = PenFor(textView, marker.Diagnostic.Severity);
            if (pen is null)
            {
                continue;
            }

            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, marker, false))
            {
                DrawSquiggle(drawingContext, pen, Math.Max(rect.Left, 0), Math.Min(rect.Right, textView.Bounds.Width), rect.Bottom - 2);
                if (++drawn >= MaxSegmentsPerFrame)
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        markers.Clear();
        markers.Disconnect(document);
    }

    private Pen? PenFor(TextView textView, DiagnosticSeverity severity)
    {
        var slot = (int)severity;
        if ((uint)slot >= (uint)pens.Length)
        {
            return null;
        }

        if (pens[slot] is { } cached)
        {
            return cached;
        }

        var key = severity switch
        {
            DiagnosticSeverity.Error => "DangerBrush",
            DiagnosticSeverity.Warning => "WarningBrush",
            DiagnosticSeverity.Information => "AccentBrush",
            _ => "TextMutedBrush",
        };
        if (!textView.TryFindResource(key, textView.ActualThemeVariant, out var resource) || resource is not IBrush brush)
        {
            return null;
        }

        return pens[slot] = new Pen(brush, 1);
    }

    private static void DrawSquiggle(DrawingContext context, Pen pen, double left, double right, double y)
    {
        if (right <= left)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(new Point(left, y), false);
            var x = left;
            var down = true;
            while (x < right)
            {
                x = Math.Min(x + 2, right);
                figure.LineTo(new Point(x, down ? y + 1.5 : y));
                down = !down;
            }

            figure.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private sealed class Marker : TextSegment
    {
        public Marker(EditorLspDiagnostic diagnostic)
        {
            Diagnostic = diagnostic;
            StartOffset = diagnostic.Start;
            Length = diagnostic.End - diagnostic.Start;
        }

        public EditorLspDiagnostic Diagnostic { get; }
    }
}
