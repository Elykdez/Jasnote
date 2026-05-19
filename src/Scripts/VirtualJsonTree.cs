using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Jesnote.Core;

namespace Jesnote;

public sealed class VirtualJsonTree : Control
{
    #region Constants and Statics

    const double RowHeight = 22;
    const double IndentWidth = 18;
    const int RowStringCap = 200;

    static readonly FontFamily s_font = new(AppInfo.MonospaceFont);
    static readonly Typeface s_typeface = new(s_font);

    #endregion

    #region Fields and Events

    JsonTreeDocument? _doc;
    readonly HashSet<int> _openBranches = [];
    readonly List<int> _visible = [];
    readonly List<int> _depths = [];
    int _firstVisibleRow;
    int _selectedRow = -1;

    // Full selection set, used for multi-select export and as the source of
    // truth for paint highlights. Always contains <see cref="SelectedId"/>
    // when single-select; Ctrl/Shift+click grows or rewrites it.
    readonly HashSet<int> _selectedIds = [];

    // Last synthetic-root child appended to _visible. Streaming loads only
    // append new top-level children; we resume from this id rather than
    // rebuilding the whole visible list each grow tick. Reset by Document
    // setter and RebuildVisible.
    int _streamingRootTailRendered = -1;

    public event EventHandler<int>? SelectionChanged;
    public event EventHandler? ScrollInfoChanged;

    #endregion

    #region Lifecycle

    public VirtualJsonTree()
    {
        Focusable = true;
        ClipToBounds = true;
        DoubleTapped += (s, e) => ToggleSelectedBranch();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property.Name == nameof(ActualThemeVariant))
            InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SetFirstVisibleRow(_firstVisibleRow, invalidate: false, notify: true);
        InvalidateVisual();
    }

    #endregion

    #region Properties

    public JsonTreeDocument? Document
    {
        get => _doc;
        set
        {
            _doc = value;
            _openBranches.Clear();
            _visible.Clear();
            _depths.Clear();
            _firstVisibleRow = 0;
            _selectedRow = -1;
            _selectedIds.Clear();
            _streamingRootTailRendered = -1;
            if (_doc != null)
            {
                // Pre-open root so any subsequent OnDocumentGrew can start
                // appending top-level children immediately. RebuildVisible is
                // a no-op when Count == 0 (streaming-load attach point).
                _openBranches.Add(JsonTreeDocument.RootId);
                if (_doc.Count > 0)
                    RebuildVisible();
            }
            OnScrollInfoChanged();
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Called from the UI thread when the document publishes more nodes.
    /// Appends any newly-published direct children of the synthetic root to
    /// the visible list without rebuilding it; deeper expansions remain
    /// untouched. Cheap: O(new root children) per call.
    /// </summary>
    public void OnDocumentGrew()
    {
        if (_doc == null)
            return;
        int published = _doc.Count;
        if (published == 0)
        {
            OnScrollInfoChanged();
            return;
        }

        int rootId = JsonTreeDocument.RootId;
        if (!_openBranches.Contains(rootId))
        {
            OnScrollInfoChanged();
            InvalidateVisual();
            return;
        }

        int cur =
            _streamingRootTailRendered == -1
                ? _doc.FirstChild[rootId]
                : _doc.NextSibling[_streamingRootTailRendered];

        int added = 0;
        while (cur != -1 && cur < published)
        {
            _visible.Add(cur);
            _depths.Add(0);
            _streamingRootTailRendered = cur;
            cur = _doc.NextSibling[cur];
            added++;
        }

        if (added > 0)
            SetFirstVisibleRow(_firstVisibleRow, invalidate: false, notify: false);
        OnScrollInfoChanged();
        InvalidateVisual();
    }

    public int SelectedId =>
        _selectedRow >= 0 && _selectedRow < _visible.Count ? _visible[_selectedRow] : -1;

    /// <summary>
    /// All currently selected node ids. Empty when nothing is selected;
    /// contains <see cref="SelectedId"/> plus any Ctrl/Shift extensions.
    /// </summary>
    public IReadOnlyCollection<int> SelectedIds => _selectedIds;

    public int FirstDocumentId => _visible.Count > 0 ? _visible[0] : -1;

    int VisibleRowCount => Math.Max(1, (int)Math.Floor(Bounds.Height / RowHeight));

    public double ScrollValue => _firstVisibleRow;

    public double ScrollMaximum => Math.Max(0, _visible.Count - VisibleRowCount);

    public double ScrollViewportSize => Math.Min(VisibleRowCount, Math.Max(1, _visible.Count));

    #endregion

    #region Scrolling

    public void SetScrollValue(double value)
    {
        SetFirstVisibleRow((int)Math.Round(value));
    }

    public void ScrollToTop()
    {
        SetFirstVisibleRow(0);
    }

    public void ScrollToBottom()
    {
        SetFirstVisibleRow((int)ScrollMaximum);
    }

    void SetFirstVisibleRow(int row, bool invalidate = true, bool notify = true)
    {
        int clamped = Math.Clamp(row, 0, (int)ScrollMaximum);
        if (clamped == _firstVisibleRow)
        {
            if (notify)
                OnScrollInfoChanged();
            return;
        }

        _firstVisibleRow = clamped;
        if (notify)
            OnScrollInfoChanged();
        if (invalidate)
            InvalidateVisual();
    }

    void EnsureSelectedRowInViewport()
    {
        if (_selectedRow < 0)
            return;

        if (_selectedRow < _firstVisibleRow)
            SetFirstVisibleRow(_selectedRow, invalidate: false, notify: false);
        else if (_selectedRow >= _firstVisibleRow + VisibleRowCount)
            SetFirstVisibleRow(
                Math.Max(0, _selectedRow - VisibleRowCount + 1),
                invalidate: false,
                notify: false
            );
        OnScrollInfoChanged();
    }

    void OnScrollInfoChanged()
    {
        ScrollInfoChanged?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region Branch Management

    public void OpenAll()
    {
        if (_doc == null)
            return;

        _openBranches.Clear();
        for (int i = 0; i < _doc.Count; i++)
        {
            if (_doc.IsBranch(i))
                _openBranches.Add(i);
        }
        RebuildVisible();
        OnScrollInfoChanged();
        InvalidateVisual();
    }

    public void CloseAll()
    {
        if (_doc == null)
            return;

        _openBranches.Clear();
        _openBranches.Add(JsonTreeDocument.RootId);
        SetFirstVisibleRow(0, invalidate: false, notify: false);
        RebuildVisible();
        OnScrollInfoChanged();
        InvalidateVisual();
    }

    void OpenBranch(int row, int id)
    {
        if (_doc == null || !_doc.IsBranch(id) || !_openBranches.Add(id))
            return;

        int published = _doc.Count;
        int depth = _depths[row];
        var ids = new List<int>();
        var depths = new List<int>();
        var stack = new Stack<(int Id, int Depth)>();
        PushChildrenReversed(id, depth + 1, stack, published);
        while (stack.Count > 0)
        {
            var (child, childDepth) = stack.Pop();
            ids.Add(child);
            depths.Add(childDepth);
            if (_doc.IsBranch(child) && _openBranches.Contains(child))
                PushChildrenReversed(child, childDepth + 1, stack, published);
        }

        _visible.InsertRange(row + 1, ids);
        _depths.InsertRange(row + 1, depths);
        OnScrollInfoChanged();
    }

    void CloseBranch(int row, int id)
    {
        if (_doc == null || !_openBranches.Remove(id))
            return;

        int depth = _depths[row];
        int end = row + 1;
        while (end < _visible.Count && _depths[end] > depth)
            end++;

        int removeCount = end - row - 1;
        if (removeCount > 0)
        {
            _visible.RemoveRange(row + 1, removeCount);
            _depths.RemoveRange(row + 1, removeCount);
        }

        if (_selectedRow > row && _selectedRow < end)
            _selectedRow = row;
        else if (_selectedRow >= end)
            _selectedRow -= removeCount;
        SetFirstVisibleRow(_firstVisibleRow, invalidate: false, notify: false);
        OnScrollInfoChanged();
    }

    void ToggleSelectedBranch()
    {
        if (_doc == null || _selectedRow < 0 || _selectedRow >= _visible.Count)
            return;

        ToggleBranch(_selectedRow);
    }

    void ToggleBranch(int row)
    {
        int id = _visible[row];
        if (_doc == null || !_doc.IsBranch(id))
            return;

        if (_openBranches.Contains(id))
            CloseBranch(row, id);
        else
            OpenBranch(row, id);
        InvalidateVisual();
    }

    #endregion

    #region Selection

    public void SelectId(int id)
    {
        if (_doc == null)
            return;

        EnsureVisible(id);
        int row = _visible.IndexOf(id);
        if (row < 0)
            return;

        _selectedRow = row;
        _selectedIds.Clear();
        _selectedIds.Add(id);
        EnsureSelectedRowInViewport();
        SelectionChanged?.Invoke(this, id);
        InvalidateVisual();
    }

    public void EnsureVisible(int id)
    {
        if (_doc == null || id < 0 || id >= _doc.Count)
            return;

        foreach (int parent in _doc.Path(id))
        {
            if (_openBranches.Contains(parent))
                continue;

            int row = _visible.IndexOf(parent);
            if (row >= 0)
                OpenBranch(row, parent);
        }

        int target = _visible.IndexOf(id);
        if (target >= 0)
        {
            if (target < _firstVisibleRow)
                SetFirstVisibleRow(target, invalidate: false, notify: false);
            else if (target >= _firstVisibleRow + VisibleRowCount)
                SetFirstVisibleRow(
                    Math.Max(0, target - VisibleRowCount + 1),
                    invalidate: false,
                    notify: false
                );
        }
        OnScrollInfoChanged();
        InvalidateVisual();
    }

    #endregion

    #region Visible Row Maintenance

    /// <summary>
    /// Forces a full rebuild of the visible row list and repaints. Used
    /// after a document mutation (e.g. union) where existing branches gained
    /// children or siblings beyond what incremental tracking can catch.
    /// </summary>
    public void RefreshVisible()
    {
        RebuildVisible();
        OnScrollInfoChanged();
        InvalidateVisual();
    }

    void RebuildVisible()
    {
        _visible.Clear();
        _depths.Clear();
        _streamingRootTailRendered = -1;
        if (_doc == null || _doc.Count == 0)
            return;

        int published = _doc.Count;
        var stack = new Stack<(int Id, int Depth)>();
        PushChildrenReversed(JsonTreeDocument.RootId, 0, stack, published);
        while (stack.Count > 0)
        {
            var (id, depth) = stack.Pop();
            _visible.Add(id);
            _depths.Add(depth);
            if (depth == 0)
                _streamingRootTailRendered = id;

            if (_doc.IsBranch(id) && _openBranches.Contains(id))
                PushChildrenReversed(id, depth + 1, stack, published);
        }

        if (_selectedRow >= _visible.Count)
            _selectedRow = _visible.Count - 1;
        SetFirstVisibleRow(_firstVisibleRow, invalidate: false, notify: false);
    }

    // published is the snapshot of _doc.Count taken by the caller. The chain
    // may extend past published while a streaming load is mid-publish; we
    // must stop at the boundary or risk reading half-initialised nodes.
    void PushChildrenReversed(
        int parentId,
        int depth,
        Stack<(int Id, int Depth)> stack,
        int published
    )
    {
        int child = _doc!.FirstChild[parentId];
        if (child == -1 || child >= published)
            return;

        int count = 0;
        for (int cur = child; cur != -1 && cur < published; cur = _doc.NextSibling[cur])
            count++;

        Span<int> kids = count <= 64 ? stackalloc int[count] : new int[count];
        int index = 0;
        for (int cur = child; cur != -1 && cur < published; cur = _doc.NextSibling[cur])
            kids[index++] = cur;

        for (int i = count - 1; i >= 0; i--)
            stack.Push((kids[i], depth));
    }

    #endregion

    #region Rendering

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var palette = Palette.For(ActualThemeVariant);
        context.FillRectangle(palette.WindowBrush, Bounds);

        if (_doc == null || _visible.Count == 0)
            return;

        int last = Math.Min(_visible.Count, _firstVisibleRow + VisibleRowCount + 1);
        for (int row = _firstVisibleRow; row < last; row++)
        {
            double y = (row - _firstVisibleRow) * RowHeight;
            int id = _visible[row];
            int depth = _depths[row];
            bool selected = _selectedIds.Contains(id);
            // ModifiedIds is empty in steady state; a HashSet.Contains hit is O(1).
            bool modified = _doc.ModifiedIds.Count > 0 && _doc.ModifiedIds.Contains(id);

            if (modified && !selected)
                context.FillRectangle(
                    palette.ModifiedBackBrush,
                    new Rect(0, y, Bounds.Width, RowHeight)
                );

            if (selected)
                context.FillRectangle(
                    palette.SelectedBackBrush,
                    new Rect(0, y, Bounds.Width, RowHeight)
                );

            var textBrush = selected ? palette.SelectedTextBrush : palette.TextBrush;
            double x = 4 + depth * IndentWidth;
            if (_doc.IsBranch(id))
            {
                string glyph = _openBranches.Contains(id) ? "[-]" : "[+]";
                DrawText(context, glyph, textBrush, x, y + 2);
            }

            x += IndentWidth + 12;
            string key = TreeRowPreview(_doc.KeyOf(id));
            if (!string.IsNullOrEmpty(key))
                x += DrawText(
                    context,
                    key + " : ",
                    selected ? palette.SelectedTextBrush : palette.KeyBrush,
                    x,
                    y + 2
                );

            DrawText(
                context,
                TreeRowPreview(ValueDisplay(id)),
                selected ? palette.SelectedTextBrush : ValueBrush(id, palette),
                x,
                y + 2
            );
        }
    }

    string ValueDisplay(int id)
    {
        var type = _doc!.TypeOf(id);
        if (type == JsonNodeType.Object)
        {
            if (_openBranches.Contains(id))
                return _doc.FirstChild[id] == -1 ? "{}" : "";
            return _doc.FirstChild[id] == -1 ? "{}" : "{...}";
        }
        if (type == JsonNodeType.Array)
        {
            if (_openBranches.Contains(id))
                return _doc.FirstChild[id] == -1 ? "[]" : "";
            return _doc.FirstChild[id] == -1 ? "[]" : "[...]";
        }
        return _doc.DisplayValueCapped(id, RowStringCap);
    }

    IBrush ValueBrush(int id, Palette palette) =>
        _doc!.TypeOf(id) switch
        {
            JsonNodeType.String => palette.StringBrush,
            JsonNodeType.Number => palette.NumberBrush,
            JsonNodeType.Boolean or JsonNodeType.Null => palette.BoolNullBrush,
            _ => palette.TextBrush,
        };

    static string TreeRowPreview(string text)
    {
        if (text.IndexOf('\r') < 0 && text.IndexOf('\n') < 0)
            return text;

        return string.Create(
            text.Length,
            text,
            static (buffer, source) =>
            {
                for (int i = 0; i < source.Length; i++)
                {
                    char c = source[i];
                    buffer[i] = c is '\r' or '\n' ? ' ' : c;
                }
            }
        );
    }

    static double DrawText(DrawingContext context, string text, IBrush brush, double x, double y)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            s_typeface,
            13,
            brush
        );
        context.DrawText(formatted, new Point(x, y));
        return formatted.Width;
    }

    #endregion

    #region Input Handling

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_doc == null || _visible.Count == 0)
            return;

        var point = e.GetPosition(this);
        int row = _firstVisibleRow + (int)(point.Y / RowHeight);
        if (row < 0 || row >= _visible.Count)
            return;

        int id = _visible[row];
        int depth = _depths[row];
        var mods = e.KeyModifiers;
        bool ctrl = (mods & KeyModifiers.Control) != 0;
        bool shift = (mods & KeyModifiers.Shift) != 0;

        if (shift && _selectedRow >= 0 && _selectedRow < _visible.Count)
        {
            // Range select: clear and add every visible id between the prior
            // primary and the click target, inclusive. Primary moves to the
            // click target so the next shift-click anchors there.
            int lo = Math.Min(_selectedRow, row);
            int hi = Math.Max(_selectedRow, row);
            _selectedIds.Clear();
            for (int r = lo; r <= hi; r++)
                _selectedIds.Add(_visible[r]);
            _selectedRow = row;
        }
        else if (ctrl)
        {
            // Toggle this id in/out of the set without disturbing the rest.
            // Primary updates to the click target either way so subsequent
            // shift-clicks anchor here.
            if (!_selectedIds.Add(id))
                _selectedIds.Remove(id);
            _selectedRow = row;
        }
        else
        {
            _selectedRow = row;
            _selectedIds.Clear();
            _selectedIds.Add(id);
        }

        double glyphX = 4 + depth * IndentWidth;
        if (_doc.IsBranch(id) && point.X >= glyphX && point.X <= glyphX + 28)
            ToggleBranch(row);

        SelectionChanged?.Invoke(this, id);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_visible.Count == 0)
            return;

        int delta = e.Delta.Y > 0 ? -3 : 3;
        SetFirstVisibleRow(_firstVisibleRow + delta, invalidate: false, notify: true);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_doc == null || _visible.Count == 0)
            return;

        int prev = _selectedRow;
        switch (e.Key)
        {
            case Key.Up:
                _selectedRow = Math.Max(0, _selectedRow - 1);
                break;
            case Key.Down:
                _selectedRow =
                    _selectedRow < 0 ? 0 : Math.Min(_visible.Count - 1, _selectedRow + 1);
                break;
            case Key.PageUp:
                _selectedRow = Math.Max(0, _selectedRow - VisibleRowCount);
                break;
            case Key.PageDown:
                _selectedRow =
                    _selectedRow < 0
                        ? Math.Min(_visible.Count - 1, VisibleRowCount - 1)
                        : Math.Min(_visible.Count - 1, _selectedRow + VisibleRowCount);
                break;
            case Key.Home:
                _selectedRow = 0;
                break;
            case Key.End:
                _selectedRow = _visible.Count - 1;
                break;
            case Key.Right:
                if (
                    _selectedRow >= 0
                    && _doc.IsBranch(_visible[_selectedRow])
                    && !_openBranches.Contains(_visible[_selectedRow])
                )
                    ToggleBranch(_selectedRow);
                break;
            case Key.Left:
                if (_selectedRow >= 0)
                {
                    int id = _visible[_selectedRow];
                    if (_doc.IsBranch(id) && _openBranches.Contains(id))
                    {
                        ToggleBranch(_selectedRow);
                    }
                    else
                    {
                        int parent = _doc.ParentOf(id);
                        int row = _visible.IndexOf(parent);
                        if (row >= 0)
                            _selectedRow = row;
                    }
                }
                break;
            case Key.Space:
            case Key.Enter:
                ToggleSelectedBranch();
                break;
            default:
                return;
        }

        EnsureSelectedRowInViewport();
        if (_selectedRow != prev && _selectedRow >= 0)
        {
            int id = _visible[_selectedRow];
            _selectedIds.Clear();
            _selectedIds.Add(id);
            SelectionChanged?.Invoke(this, id);
        }
        e.Handled = true;
        InvalidateVisual();
    }

    #endregion

    #region Types & Helpers

    sealed class Palette
    {
        public required IBrush WindowBrush { get; init; }
        public required IBrush TextBrush { get; init; }
        public required IBrush KeyBrush { get; init; }
        public required IBrush StringBrush { get; init; }
        public required IBrush NumberBrush { get; init; }
        public required IBrush BoolNullBrush { get; init; }
        public required IBrush SelectedBackBrush { get; init; }
        public required IBrush SelectedTextBrush { get; init; }
        public required IBrush ModifiedBackBrush { get; init; }

        public static Palette For(ThemeVariant variant) =>
            variant == ThemeVariant.Dark ? Dark : Light;

        static readonly Palette Light =
            new()
            {
                WindowBrush = Brushes.White,
                TextBrush = Brushes.Black,
                KeyBrush = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                StringBrush = new SolidColorBrush(Color.FromRgb(176, 121, 0)),
                NumberBrush = new SolidColorBrush(Color.FromRgb(0, 128, 0)),
                BoolNullBrush = new SolidColorBrush(Color.FromRgb(192, 32, 32)),
                SelectedBackBrush = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                SelectedTextBrush = Brushes.White,
                ModifiedBackBrush = new SolidColorBrush(Color.FromArgb(40, 255, 200, 0)),
            };

        static readonly Palette Dark =
            new()
            {
                WindowBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                TextBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                KeyBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                StringBrush = new SolidColorBrush(Color.FromRgb(230, 180, 80)),
                NumberBrush = new SolidColorBrush(Color.FromRgb(120, 200, 120)),
                BoolNullBrush = new SolidColorBrush(Color.FromRgb(230, 110, 110)),
                SelectedBackBrush = new SolidColorBrush(Color.FromRgb(38, 79, 120)),
                SelectedTextBrush = Brushes.White,
                ModifiedBackBrush = new SolidColorBrush(Color.FromArgb(64, 230, 180, 80)),
            };
    }

    #endregion
}
