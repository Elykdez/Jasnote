using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Jesnote.Core;

namespace Jesnote;

public sealed class MainWindow : Window, ILocalizable
{
    #region Constants and Statics

    const int DetailStringCap = 64 * 1024;
    const int ProgressUiMinIntervalMs = 33;

    static readonly SearchTypeChoice[] s_searchTypes =
    [
        new(SearchType.Key),
        new(SearchType.Keyword),
        new(SearchType.Number),
        new(SearchType.String),
    ];

    static readonly ColorThemePreference[] s_themeOrder =
    [
        ColorThemePreference.Auto,
        ColorThemePreference.Light,
        ColorThemePreference.Dark,
    ];

    static readonly LanguagePreference[] s_languagePreferences =
    [
        LanguagePreference.English,
        LanguagePreference.ChineseSimplified,
        LanguagePreference.Spanish,
        LanguagePreference.Portuguese,
        LanguagePreference.French,
        LanguagePreference.Russian,
        LanguagePreference.Japanese,
        LanguagePreference.Korean,
    ];

    static readonly StringStorageMode[] s_storageModes =
    [
        StringStorageMode.Compact,
        StringStorageMode.Classic,
    ];

    enum EditPanelMode
    {
        Hidden,
        EditValue,
        AddChild,
    }

    // Primitive-typed options shown in the Add-child type combo.
    static readonly JsonNodeType[] s_addNodeTypes =
    [
        JsonNodeType.Object,
        JsonNodeType.Array,
        JsonNodeType.String,
        JsonNodeType.Number,
        JsonNodeType.Boolean,
        JsonNodeType.Null,
    ];

    #endregion

    #region Fields

    readonly AppSettings _settings;
    readonly JsonTreeDocument _doc = new();

    readonly Menu _menu = new();
    readonly MenuItem _fileMenu = new();
    readonly MenuItem _fileNew = new();
    readonly MenuItem _fileOpen = new();
    readonly MenuItem _fileOpenClipboard = new();
    readonly MenuItem _fileOpenRecent = new();
    readonly MenuItem _fileReload = new();
    readonly MenuItem _fileSave = new();
    readonly MenuItem _fileSaveAs = new();
    readonly MenuItem _fileUnion = new();
    readonly MenuItem _fileExportFile = new();
    readonly MenuItem _fileExportClipboard = new();
    readonly MenuItem _fileSettings = new();
    readonly MenuItem _fileQuit = new();
    readonly MenuItem _viewMenu = new();
    readonly MenuItem _viewExpandAll = new();
    readonly MenuItem _viewCollapseAll = new();
    readonly MenuItem _goMenu = new();
    readonly MenuItem _goTop = new();
    readonly MenuItem _goBottom = new();
    readonly MenuItem _goSelection = new();
    readonly MenuItem _helpMenu = new();
    readonly MenuItem _helpCheckUpdates = new();
    readonly MenuItem _helpReport = new();
    readonly MenuItem _helpAbout = new();

    readonly ComboBox _searchType =
        new()
        {
            Width = 100,
            ItemsSource = s_searchTypes,
            SelectedIndex = 0,
        };
    readonly TextBox _searchText = new();
    readonly Button _searchButton = new() { MinWidth = 54 };
    readonly Button _topButton = new() { Width = 32, MinWidth = 32 };
    readonly Button _bottomButton = new() { Width = 32, MinWidth = 32 };
    readonly Button _collapseButton = new() { MinWidth = 72 };
    readonly Button _editButton = new() { MinWidth = 54, Width = 60 };
    readonly Button _addButton = new() { MinWidth = 54, Width = 60 };
    readonly Button _deleteButton = new() { MinWidth = 54, Width = 60 };
    readonly Button _undoButton = new() { MinWidth = 32, Width = 32 };
    readonly Button _redoButton = new() { MinWidth = 32, Width = 32 };

    readonly VirtualJsonTree _tree = new();
    readonly ScrollBar _treeScrollBar =
        new()
        {
            Orientation = Orientation.Vertical,
            Width = 16,
            SmallChange = 3,
            IsVisible = false,
        };
    readonly TextBox _detail =
        new()
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily(AppInfo.MonospaceFont),
        };
    readonly TextBlock _status = new();
    readonly TextBlock _welcome =
        new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480,
            Opacity = 0.65,
            IsHitTestVisible = false,
        };
    readonly TextBlock _updateLink = CreateLinkText(
        "",
        AppInfo.ReleaseUrl,
        isVisible: false,
        margin: new Thickness(8, 0, 0, 0)
    );
    readonly Button _cancelLoadButton =
        new()
        {
            IsVisible = false,
            MinWidth = 86,
            Margin = new Thickness(8, 0, 0, 0),
        };

    // Edit chrome - hidden in view mode. The actual value input is the
    // _detail TextBox itself; only the meta-controls (title/type/key/buttons)
    // live in this panel, so editing and previewing share the same input area.
    readonly Panel _editPanel = new StackPanel { IsVisible = false, Spacing = 4 };
    readonly TextBlock _editTitle = new() { FontWeight = FontWeight.SemiBold };
    readonly ComboBox _editTypeCombo = new() { MinWidth = 110 };
    readonly TextBox _editKey = new();
    readonly Button _editApply = new() { MinWidth = 72 };
    readonly Button _editCancel = new() { MinWidth = 72 };
    EditPanelMode _editMode = EditPanelMode.Hidden;
    int _editTargetId = -1;

    string? _currentFile;
    string? _currentLabel;
    TimeSpan? _lastLoadDuration;
    CancellationTokenSource? _loadCts;
    bool _settingsReady;
    bool _updatingTreeScrollBar;
    bool _busy;

    // Fires on the parser thread. We coalesce on a single pending UI post so
    // a burst of grow events (one per ~33 ms in the parser) maps to at most
    // one UI-thread refresh per dispatcher tick.
    int _growPostPending;

    #endregion

    #region Lifecycle

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        _settingsReady = true;
        Title = AppInfo.AppName;
        RequestedThemeVariant = CurrentRequestedThemeVariant();
        ApplyWindowIcon(this);
        WindowChrome.AttachTo(this, () => IsDarkChrome(this));
        Width = Math.Max(720, _settings.LastWindowWidth);
        Height = Math.Max(520, _settings.LastWindowHeight);
        MinWidth = 720;
        MinHeight = 520;

        Content = BuildLayout();
        BuildMenu();
        ApplyLocalization();
        RebuildRecentFilesMenu();
        SetHasDocument(false);

        _tree.SelectionChanged += (s, id) => OnSelectionChanged(id);
        _tree.ScrollInfoChanged += (s, e) => UpdateTreeScrollBar();
        // Refresh menu/toolbar state when graft/edit/save toggles IsModified, and
        // refresh the tree paint so the modified-row tint stays in sync.
        _doc.DocumentModified += () =>
            Dispatcher.UIThread.Post(() =>
            {
                SetHasDocument(_doc.Count > 0);
                SetTitle(_currentLabel);
                _tree.InvalidateVisual();
            });

        _editButton.Click += (s, e) => StartEdit();
        _addButton.Click += (s, e) => StartAddChild();
        _deleteButton.Click += (s, e) => DoDelete();
        _undoButton.Click += (s, e) => DoUndo();
        _redoButton.Click += (s, e) => DoRedo();
        _editApply.Click += async (s, e) => await OnEditApplyAsync();
        _editCancel.Click += (s, e) => OnEditCancel();
        _treeScrollBar.ValueChanged += (s, e) =>
        {
            if (!_updatingTreeScrollBar)
                _tree.SetScrollValue(e.NewValue);
        };
        _searchButton.Click += async (s, e) => await DoSearchAsync();
        _searchText.KeyDown += async (s, e) =>
        {
            if (e.Key == Key.Enter)
                await DoSearchAsync();
        };
        _topButton.Click += (s, e) => _tree.ScrollToTop();
        _bottomButton.Click += (s, e) => _tree.ScrollToBottom();
        _collapseButton.Click += (s, e) => _tree.CloseAll();
        _cancelLoadButton.Click += (s, e) => CancelActiveLoad();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        Closed += (s, e) =>
        {
            _settings.LastWindowWidth = Math.Max(720, (int)Bounds.Width);
            _settings.LastWindowHeight = Math.Max(520, (int)Bounds.Height);
            _settings.Save();
        };

        Opened += (s, e) =>
        {
            if (_settings.NotifyUpdates)
                _ = CheckUpdatesAsync();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property.Name == nameof(ActualThemeVariant))
        {
            if (!_settingsReady)
                return;
            ApplyWindowIcon(this);
            _tree.InvalidateVisual();
        }
    }

    #endregion

    #region Layout

    Control BuildLayout()
    {
        var root = new DockPanel();

        DockPanel.SetDock(_menu, Dock.Top);
        root.Children.Add(_menu);

        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var statusHost = new Border
        {
            Padding = new Thickness(8, 4),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = Brushes.LightGray,
            Child = BuildStatusBar(),
        };
        DockPanel.SetDock(statusHost, Dock.Bottom);
        root.Children.Add(statusHost);

        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,320") };
        var treeHost = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(8, 8, 0, 8),
            MinWidth = 220,
        };
        _tree.Margin = new Thickness(0);
        _treeScrollBar.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(_tree, 0);
        Grid.SetColumn(_welcome, 0);
        Grid.SetColumn(_treeScrollBar, 1);
        treeHost.Children.Add(_tree);
        treeHost.Children.Add(_welcome);
        treeHost.Children.Add(_treeScrollBar);

        var splitter = new GridSplitter
        {
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
        };

        _detail.Margin = new Thickness(0);
        _detail.MinWidth = 180;
        var detailColumn = BuildDetailColumn();
        Grid.SetColumn(treeHost, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(detailColumn, 2);
        main.Children.Add(treeHost);
        main.Children.Add(splitter);
        main.Children.Add(detailColumn);
        root.Children.Add(main);

        return root;
    }

    Control BuildDetailColumn()
    {
        var host = new DockPanel { Margin = new Thickness(4, 8, 8, 8), MinWidth = 180 };

        // Inline edit panel docks to the bottom; collapses to zero height when hidden.
        BuildEditPanel();
        DockPanel.SetDock(_editPanel, Dock.Bottom);
        host.Children.Add(_editPanel);

        host.Children.Add(_detail);
        return host;
    }

    void BuildEditPanel()
    {
        var stack = (StackPanel)_editPanel;
        stack.Margin = new Thickness(0, 8, 0, 0);

        // Apply / Cancel are wired to OnEditApply / OnEditCancel further down.
        _editApply.HorizontalAlignment = HorizontalAlignment.Left;
        _editCancel.HorizontalAlignment = HorizontalAlignment.Left;

        _editTypeCombo.ItemsSource = s_addNodeTypes;
        _editTypeCombo.SelectedIndex = 2; // String by default
        _editTypeCombo.SelectionChanged += (s, e) => UpdateEditValueEnabled();

        _editKey.PlaceholderText = "key";
        _editKey.KeyDown += (s, e) => HandleEditKey(e);
        // The detail textbox doubles as the value editor in edit mode; it
        // already lives in the layout below this chrome panel, so we wire
        // Enter/Escape only while we are actively editing - see StartEdit
        // and StartAddChild for the AddHandler/RemoveHandler dance.

        var typeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { _editTypeCombo },
        };
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { _editApply, _editCancel },
        };

        stack.Children.Add(_editTitle);
        stack.Children.Add(typeRow);
        stack.Children.Add(_editKey);
        stack.Children.Add(buttonRow);
    }

    void HandleEditKey(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = OnEditApplyAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            OnEditCancel();
            e.Handled = true;
        }
    }

    Grid BuildStatusBar()
    {
        var status = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        _status.VerticalAlignment = VerticalAlignment.Center;
        status.Children.Add(_status);
        Grid.SetColumn(_updateLink, 1);
        status.Children.Add(_updateLink);
        Grid.SetColumn(_cancelLoadButton, 2);
        status.Children.Add(_cancelLoadButton);
        return status;
    }

    Grid BuildToolbar()
    {
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(
                "Auto,*,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"
            ),
            Margin = new Thickness(8, 8, 8, 0),
        };

        AddToolbarChild(toolbar, _searchType, 0);
        AddToolbarChild(toolbar, _searchText, 1);
        AddToolbarChild(toolbar, _searchButton, 2);
        AddToolbarChild(toolbar, _topButton, 3);
        AddToolbarChild(toolbar, _bottomButton, 4);
        AddToolbarChild(toolbar, _collapseButton, 5);
        AddToolbarChild(toolbar, _undoButton, 6);
        AddToolbarChild(toolbar, _redoButton, 7);
        AddToolbarChild(toolbar, _editButton, 8);
        AddToolbarChild(toolbar, _addButton, 9);
        AddToolbarChild(toolbar, _deleteButton, 10);

        return toolbar;
    }

    static void AddToolbarChild(Grid toolbar, Control control, int column)
    {
        control.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(control, column);
        toolbar.Children.Add(control);
    }

    void UpdateTreeScrollBar()
    {
        _updatingTreeScrollBar = true;
        try
        {
            double maximum = _tree.ScrollMaximum;
            _treeScrollBar.Maximum = maximum;
            _treeScrollBar.ViewportSize = _tree.ScrollViewportSize;
            _treeScrollBar.LargeChange = Math.Max(1, _tree.ScrollViewportSize);
            _treeScrollBar.Value = Math.Clamp(_tree.ScrollValue, 0, maximum);
            _treeScrollBar.IsVisible = maximum > 0;
        }
        finally
        {
            _updatingTreeScrollBar = false;
        }
    }

    #endregion

    #region Menu

    void BuildMenu()
    {
        ApplyMenuHotKeys();

        _fileNew.Click += (s, e) => DoNew();
        _fileOpen.Click += async (s, e) => await DoOpenFileAsync();
        _fileOpenClipboard.Click += async (s, e) => await DoOpenFromClipboardAsync();
        _fileReload.Click += async (s, e) => await DoReloadAsync();
        _fileSave.Click += async (s, e) => await DoSaveAsync();
        _fileSaveAs.Click += async (s, e) => await DoSaveAsAsync();
        _fileUnion.Click += async (s, e) => await DoUnionWithAsync();
        _fileExportFile.Click += async (s, e) => await DoExportToFileAsync();
        _fileExportClipboard.Click += async (s, e) => await DoExportToClipboardAsync();
        _fileSettings.Click += async (s, e) => await ShowSettingsAsync();
        _fileQuit.Click += (s, e) => Close();

        _viewExpandAll.Click += (s, e) => DoExpandAll();
        _viewCollapseAll.Click += (s, e) => _tree.CloseAll();
        _goTop.Click += (s, e) => _tree.ScrollToTop();
        _goBottom.Click += (s, e) => _tree.ScrollToBottom();
        _goSelection.Click += (s, e) =>
        {
            if (_tree.SelectedId >= 0)
                _tree.EnsureVisible(_tree.SelectedId);
        };

        _helpCheckUpdates.Click += async (s, e) => await CheckUpdatesAsync(showResult: true);
        _helpReport.Click += (s, e) => OpenUrl(AppInfo.ReportUrl);
        _helpAbout.Click += async (s, e) => await ShowAboutAsync();

        _fileMenu.ItemsSource = new Control[]
        {
            _fileNew,
            new Separator(),
            _fileOpen,
            _fileOpenRecent,
            _fileOpenClipboard,
            _fileReload,
            new Separator(),
            _fileSave,
            _fileSaveAs,
            _fileUnion,
            new Separator(),
            _fileExportFile,
            _fileExportClipboard,
            new Separator(),
            _fileSettings,
            _fileQuit,
        };
        _viewMenu.ItemsSource = new Control[] { _viewExpandAll, _viewCollapseAll };
        _goMenu.ItemsSource = new Control[] { _goTop, _goBottom, _goSelection };
        _helpMenu.ItemsSource = new Control[]
        {
            _helpCheckUpdates,
            _helpReport,
            new Separator(),
            _helpAbout,
        };
        _menu.ItemsSource = new Control[] { _fileMenu, _viewMenu, _goMenu, _helpMenu };
    }

    void ApplyMenuHotKeys()
    {
        // Avalonia renders InputGesture in the right column automatically; HotKey is the actual keystroke trigger.
        // Always set both via SetShortcut so the visible label cannot drift from the binding that fires.
        SetShortcut(_fileNew, new KeyGesture(Key.N, KeyModifiers.Control));
        SetShortcut(_fileOpen, new KeyGesture(Key.O, KeyModifiers.Control));
        SetShortcut(_fileReload, new KeyGesture(Key.R, KeyModifiers.Alt));
        SetShortcut(_fileSave, new KeyGesture(Key.S, KeyModifiers.Control));
        SetShortcut(_fileSaveAs, new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift));
        // Toolbar-button hotkeys. Window-level: fire whenever no focused
        // control consumes them first (TextBox edit-mode Ctrl+Z is preserved
        // so users can undo typing inside the edit panel).
        _editButton.HotKey = new KeyGesture(Key.F2);
        _undoButton.HotKey = new KeyGesture(Key.Z, KeyModifiers.Control);
        _redoButton.HotKey = new KeyGesture(Key.Y, KeyModifiers.Control);
        SetShortcut(_fileSettings, new KeyGesture(Key.OemComma, KeyModifiers.Control));
        SetShortcut(_fileQuit, new KeyGesture(Key.Q, KeyModifiers.Control));
        SetShortcut(_goTop, new KeyGesture(Key.Home, KeyModifiers.Control));
        SetShortcut(_goBottom, new KeyGesture(Key.End, KeyModifiers.Control));
    }

    public void ApplyLocalization()
    {
        _fileMenu.Header = Localization.T("Menu.File");
        _fileNew.Header = Localization.T("Menu.File.New");
        _fileOpen.Header = Localization.T("Menu.File.Open");
        _fileOpenClipboard.Header = Localization.T("Menu.File.OpenClipboard");
        _fileOpenRecent.Header = Localization.T("Menu.File.OpenRecent");
        _fileReload.Header = Localization.T("Menu.File.Reload");
        _fileSave.Header = Localization.T("Menu.File.Save");
        _fileSaveAs.Header = Localization.T("Menu.File.SaveAs");
        _fileUnion.Header = Localization.T("Menu.File.UnionWith");
        _fileExportFile.Header = Localization.T("Menu.File.ExportFile");
        _fileExportClipboard.Header = Localization.T("Menu.File.ExportClipboard");
        _fileSettings.Header = Localization.T("Menu.File.Settings");
        _fileQuit.Header = Localization.T("Menu.File.Quit");
        _viewMenu.Header = Localization.T("Menu.View");
        _viewExpandAll.Header = Localization.T("Menu.View.ExpandAll");
        _viewCollapseAll.Header = Localization.T("Menu.View.CollapseAll");
        _goMenu.Header = Localization.T("Menu.Go");
        _goTop.Header = Localization.T("Menu.Go.Top");
        _goBottom.Header = Localization.T("Menu.Go.Bottom");
        _goSelection.Header = Localization.T("Menu.Go.Selection");
        _helpMenu.Header = Localization.T("Menu.Help");
        _helpCheckUpdates.Header = Localization.T("Menu.Help.CheckUpdates");
        _helpReport.Header = Localization.T("Menu.Help.ReportBug");
        _helpAbout.Header = Localization.T("Menu.Help.About");

        _searchText.PlaceholderText = Localization.T("Search.Placeholder");
        _searchButton.Content = Localization.T("Search.Button");
        _topButton.Content = "↑";
        _bottomButton.Content = "↓";
        ToolTip.SetTip(_topButton, Localization.T("Tooltip.ScrollTop"));
        ToolTip.SetTip(_bottomButton, Localization.T("Tooltip.ScrollBottom"));
        _collapseButton.Content = Localization.T("Menu.View.CollapseAll");
        _editButton.Content = Localization.T("Toolbar.Edit");
        _addButton.Content = Localization.T("Toolbar.Add");
        _deleteButton.Content = Localization.T("Toolbar.Delete");
        _undoButton.Content = "↺";
        _redoButton.Content = "↻";
        ToolTip.SetTip(_undoButton, Localization.T("Tooltip.Undo"));
        ToolTip.SetTip(_redoButton, Localization.T("Tooltip.Redo"));
        _editApply.Content = Localization.T("Edit.Apply");
        _editCancel.Content = Localization.T("Edit.Cancel");
        _cancelLoadButton.Content = Localization.T("Common.Cancel");
        _welcome.Text = Localization.T("Welcome.Text");
        if (_updateLink.IsVisible)
            _updateLink.Text = Localization.T("Status.UpdateAvailable");
        UpdateSearchTypes();
        SetTitle(_currentLabel);
        UpdateElementsLabel();
    }

    void UpdateSearchTypes()
    {
        var selected = _searchType.SelectedItem is SearchTypeChoice choice
            ? choice.Type
            : SearchType.Key;
        _searchType.ItemsSource = s_searchTypes;
        _searchType.SelectedIndex = Math.Max(
            0,
            Array.FindIndex(s_searchTypes, choice => choice.Type == selected)
        );
    }

    #endregion

    #region Loading

    public void LoadInitial(string path)
    {
        _ = LoadFileAsync(path);
    }

    void DoNew()
    {
        _doc.Reset();
        _currentFile = null;
        _currentLabel = null;
        _lastLoadDuration = null;
        _tree.Document = null;
        _detail.Text = "";
        SetTitle(null);
        SetHasDocument(false);
        UpdateElementsLabel();
    }

    async Task DoOpenFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = Localization.T("FileDialog.OpenTitle"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("JSON") { Patterns = ["*.json", "*.jsonl", "*.ndjson"] },
                    FilePickerFileTypes.All,
                ],
            }
        );

        if (files.Count == 0)
            return;

        var path = files[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            await LoadFileAsync(path);
    }

    async Task DoOpenFromClipboardAsync()
    {
        var text = Clipboard == null ? null : await Clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus(Localization.T("Error.ClipboardEmpty"));
            return;
        }

        await LoadBytesAsync(
            Encoding.UTF8.GetBytes(text),
            Localization.T("Loading.ClipboardLabel")
        );
    }

    async Task DoReloadAsync()
    {
        if (!string.IsNullOrEmpty(_currentFile))
            await LoadFileAsync(_currentFile);
    }

    async Task LoadFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            SetStatus(Localization.F("Error.FileNotFound", path));
            return;
        }

        bool jsonl = IsJsonlPath(path);
        await LoadCoreAsync(
            Path.GetFileName(path),
            addRecent: true,
            path,
            (progress, ct) => _doc.LoadAsync(path, progress, ct, jsonl),
            streaming: jsonl
        );
    }

    async Task LoadBytesAsync(byte[] data, string label)
    {
        await LoadCoreAsync(
            label,
            addRecent: false,
            path: null,
            (progress, ct) => _doc.LoadAsync(data, progress, ct),
            streaming: false
        );
    }

    async Task LoadCoreAsync(
        string label,
        bool addRecent,
        string? path,
        Func<IProgress<ProgressInfo>, CancellationToken, Task> load,
        bool streaming
    )
    {
        if (_busy)
            return;

        using var cts = new CancellationTokenSource();
        _loadCts = cts;
        SetBusy(true);
        // Read the string-storage preference at load start; switching modes
        // takes effect on the next load.
        _doc.StringStorageMode = _settings.StringStorage;
        _doc.Reset();
        _tree.Document = null;
        _detail.Text = "";
        _currentFile = null;
        _currentLabel = null;
        _lastLoadDuration = null;
        SetTitle(null);
        SetHasDocument(false);
        var stopwatch = Stopwatch.StartNew();
        IProgress<ProgressInfo> progress = new ThrottledProgress(this, label);

        Action? growHandler = null;
        if (streaming)
        {
            // Attach the document before the load starts so the tree can
            // render rows the instant the first publish lands. The synthetic
            // root is empty initially; OnDocumentGrew will append children
            // as they are published.
            _tree.Document = _doc;
            growHandler = OnDocumentGrewFromWorker;
            _doc.DocumentGrew += growHandler;
        }

        try
        {
            await load(progress, cts.Token);
            stopwatch.Stop();
            _currentFile = path;
            _currentLabel = label;
            _lastLoadDuration = stopwatch.Elapsed;
            if (!streaming)
                _tree.Document = _doc;
            else
                _tree.OnDocumentGrew();
            if (_tree.SelectedId < 0 && _tree.FirstDocumentId >= 0)
                _tree.SelectId(_tree.FirstDocumentId);
            SetHasDocument(true);
            SetTitle(label);
            UpdateElementsLabel();
            if (addRecent && !string.IsNullOrEmpty(path))
            {
                _settings.PushRecentFile(path);
                _settings.Save();
                RebuildRecentFilesMenu();
            }
            // Force a full GC + LOH compaction so all parser-only garbage
            // (Utf8JsonReader scratch strings, BuildContext intern dicts,
            // resized-out arrays) is reclaimed promptly. Without this, Server
            // GC keeps committed memory high after a big load and Task
            // Manager underreports the real savings.
            //
            // GC.Collect is stop-the-world so the UI freezes for 1-2 s on
            // large heaps. We show a hint, pump the dispatcher once so the
            // hint paints, then run the collection on a worker (still freezes
            // the UI, but at least the user sees why).
            SetStatus(Localization.T("Loading.Optimizing"));
            await Task.Yield();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            await Task.Run(ReleaseParserGarbage);
            long managed = GC.GetTotalMemory(forceFullCollection: false);
            SetStatus(
                $"{_doc.Count:N0} elements loaded in {FormatDuration(stopwatch.Elapsed)} from {label} (managed heap: {FormatBytes(managed)})"
            );
        }
        catch (OperationCanceledException)
        {
            // Count == 1 means only the synthetic root was added before
            // cancellation - nothing worth keeping; fall through to reset.
            if (streaming && _doc.Count > 1)
            {
                // Keep whatever was already streamed - the synthetic root and
                // however many top-level values made it through. The user
                // can browse the partial document.
                stopwatch.Stop();
                _currentFile = path;
                _currentLabel = label;
                _lastLoadDuration = stopwatch.Elapsed;
                _tree.OnDocumentGrew();
                if (_tree.SelectedId < 0 && _tree.FirstDocumentId >= 0)
                    _tree.SelectId(_tree.FirstDocumentId);
                SetHasDocument(true);
                SetTitle(label);
                SetStatus(
                    Localization.T("Loading.Canceled") + $" {_doc.Count:N0} elements available."
                );
            }
            else
            {
                _doc.Reset();
                _tree.Document = null;
                _detail.Text = "";
                SetHasDocument(false);
                SetStatus(Localization.T("Loading.Canceled"));
            }
        }
        catch (Exception ex)
        {
            _doc.Reset();
            _tree.Document = null;
            _detail.Text = "";
            SetHasDocument(false);
            SetStatus(Localization.F("Error.OpenDocumentFailed", label) + " " + ex.Message);
        }
        finally
        {
            if (growHandler != null)
                _doc.DocumentGrew -= growHandler;
            if (ReferenceEquals(_loadCts, cts))
                _loadCts = null;
            SetBusy(false);
        }
    }

    void OnDocumentGrewFromWorker()
    {
        if (Interlocked.Exchange(ref _growPostPending, 1) == 1)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _growPostPending, 0);
            if (_tree.Document == null)
                return;
            _tree.OnDocumentGrew();
            UpdateElementsLabel();
        });
    }

    void CancelActiveLoad()
    {
        var cts = _loadCts;
        if (cts == null || cts.IsCancellationRequested)
            return;

        cts.Cancel();
        _cancelLoadButton.IsEnabled = false;
        SetStatus(Localization.T("Loading.Canceling"));
    }

    void UpdateProgress(string label, ProgressInfo info)
    {
        var pct = info.Progress > 0 ? $" {info.Progress:P0}" : "";
        string text = info.CurrentStep switch
        {
            1 => Localization.F("Loading.Progress.File", info.CurrentStep, info.TotalSteps, label),
            2 => Localization.F("Loading.Progress.Size", info.CurrentStep, info.TotalSteps, label),
            3 => Localization.F(
                "Loading.Progress.Rendering",
                info.CurrentStep,
                info.TotalSteps,
                info.Size > 0 ? FormatThousands(info.Size) : "?",
                label
            ),
            _ => Localization.F("Loading.Progress.Unknown", info.CurrentStep, info.TotalSteps),
        };
        SetStatus(text + pct);
    }

    static bool IsJsonlPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        var ext = Path.GetExtension(path);
        return ext.Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ndjson", StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Search

    async Task DoSearchAsync()
    {
        string pattern = _searchText.Text ?? "";
        if (string.IsNullOrWhiteSpace(pattern) || _doc.Count == 0)
            return;

        var type = _searchType.SelectedItem is SearchTypeChoice choice
            ? choice.Type
            : SearchType.Key;
        if (type == SearchType.Keyword)
        {
            var lower = pattern.ToLowerInvariant();
            if (lower != "true" && lower != "false" && lower != "null")
            {
                SetStatus(Localization.T("Error.SearchAllowedKeywords"));
                return;
            }
            pattern = lower;
        }

        int start = _tree.SelectedId >= 0 ? _tree.SelectedId : JsonTreeDocument.RootId;
        SetBusy(true);
        try
        {
            int found = await Task.Run(
                () => _doc.Search(start, pattern, type, CancellationToken.None)
            );
            if (found == JsonTreeDocument.NotFound)
            {
                SetStatus(
                    Localization.F(
                        "Search.NoMatch.Message",
                        Localization.SearchTypeName(type),
                        pattern
                    )
                );
                return;
            }
            _tree.SelectId(found);
            SetStatus("Matched node " + found.ToString("N0"));
        }
        catch (Exception ex)
        {
            SetStatus(Localization.T("Error.SearchFailed") + " " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    #endregion

    #region Selection and Detail

    void OnSelectionChanged(int id)
    {
        if (id < 0)
        {
            _detail.Text = "";
            return;
        }

        RenderDetail(id);
        SetHasDocument(_doc.Count > 0);
    }

    void RenderDetail(int id)
    {
        if (_doc.IsBranch(id))
        {
            int count = _doc.ChildCount(id);
            var type = _doc.TypeOf(id);
            string shown = type == JsonNodeType.Array ? "[...]" : "{...}";
            _detail.Text =
                $"{Localization.F("Detail.BranchSummary", Localization.JsonNodeTypeName(type), count)}"
                + Environment.NewLine
                + Environment.NewLine
                + shown;
            return;
        }

        string display = _doc.DisplayValueCapped(id, DetailStringCap);
        _detail.Text =
            Localization.JsonNodeTypeName(_doc.TypeOf(id))
            + Environment.NewLine
            + Environment.NewLine
            + display;
    }

    #endregion

    #region Export

    async Task DoExportToFileAsync()
    {
        if (_tree.SelectedIds.Count == 0)
            return;

        try
        {
            byte[] data = ExtractSelectionBytes();
            var file = await StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = Localization.T("FileDialog.ExportTitle"),
                    SuggestedFileName = "export.json",
                    FileTypeChoices =
                    [
                        new FilePickerFileType("JSON") { Patterns = ["*.json"] },
                        FilePickerFileTypes.All,
                    ],
                }
            );
            if (file == null)
                return;

            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(data);
            SetStatus(Localization.T("Status.Exported"));
        }
        catch (Exception ex)
        {
            SetStatus(Localization.T("Error.ExportFailed") + " " + ex.Message);
        }
    }

    async Task DoExportToClipboardAsync()
    {
        if (_tree.SelectedIds.Count == 0 || Clipboard == null)
            return;

        try
        {
            byte[] data = ExtractSelectionBytes();
            await Clipboard.SetTextAsync(Encoding.UTF8.GetString(data));
            SetStatus(Localization.T("Status.Copied"));
        }
        catch (Exception ex)
        {
            SetStatus(Localization.T("Error.ExportFailed") + " " + ex.Message);
        }
    }

    byte[] ExtractSelectionBytes()
    {
        var ids = _tree.SelectedIds;
        if (ids.Count == 0)
            throw new InvalidOperationException(Localization.T("Error.NoSelection"));

        if (ids.Count == 1)
        {
            int id = ids.First();
            // Preserve the single-export promotion: non-branch leaves export as
            // their parent so the result is always a self-contained subtree.
            var type = _doc.TypeOf(id);
            if (type != JsonNodeType.Array && type != JsonNodeType.Object)
                id = _doc.ParentOf(id);
            if (id < 0)
                id = JsonTreeDocument.RootId;
            return _doc.Extract(id);
        }

        // Multi-selection: respect literal picks (no parent promotion) and
        // wrap them in a single JSON array, regardless of what types they
        // mix.
        return _doc.ExtractMany(ids.ToArray());
    }

    #endregion

    #region Save

    async Task DoSaveAsync()
    {
        if (string.IsNullOrEmpty(_currentFile))
        {
            await DoSaveAsAsync();
            return;
        }
        await SaveToPathAsync(_currentFile);
    }

    async Task DoSaveAsAsync()
    {
        if (_doc.Count == 0)
            return;

        bool jsonl = _doc.IsJsonl;
        string defaultName = string.IsNullOrEmpty(_currentFile)
            ? (jsonl ? "document.jsonl" : "document.json")
            : Path.GetFileName(_currentFile);

        var file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = Localization.T("FileDialog.SaveTitle"),
                SuggestedFileName = defaultName,
                FileTypeChoices = jsonl
                    ?
                    [
                        new FilePickerFileType("JSON Lines") { Patterns = ["*.jsonl", "*.ndjson"] },
                        FilePickerFileTypes.All,
                    ]
                    :
                    [
                        new FilePickerFileType("JSON") { Patterns = ["*.json"] },
                        FilePickerFileTypes.All,
                    ],
            }
        );
        if (file == null)
            return;

        string? path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            SetStatus(Localization.T("Error.SaveFailed"));
            return;
        }
        await SaveToPathAsync(path);
        _currentFile = path;
        SetTitle(_currentLabel ?? Path.GetFileName(path));
    }

    async Task SaveToPathAsync(string path)
    {
        try
        {
            using var cts = new CancellationTokenSource();
            await _doc.SaveAsync(path, cts.Token);
            SetStatus(Localization.F("Status.Saved", path));
            SetHasDocument(true);
        }
        catch (Exception ex)
        {
            SetStatus(Localization.T("Error.SaveFailed") + " " + ex.Message);
        }
    }

    #endregion

    #region Union

    async Task DoUnionWithAsync()
    {
        if (_doc.Count == 0)
            return;
        int anchor = _tree.SelectedId;
        if (anchor < 0 || !_doc.CanGraftInto(anchor))
        {
            SetStatus(Localization.T("Error.GraftNoAnchor"));
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = Localization.T("FileDialog.UnionTitle"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("JSON") { Patterns = ["*.json", "*.jsonl", "*.ndjson"] },
                    FilePickerFileTypes.All,
                ],
            }
        );
        if (files.Count == 0)
            return;

        string? path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            SetStatus(Localization.T("Error.GraftLoadFailed"));
            return;
        }

        var other = new JsonTreeDocument { StringStorageMode = _doc.StringStorageMode };
        try
        {
            using var cts = new CancellationTokenSource();
            bool jsonl = IsJsonlPath(path);
            await other.LoadAsync(path, progress: null, cts.Token, jsonl);
            // Run the graft itself off the UI thread - cloning a large source
            // doc is bounded by source size and we don't want the window to
            // freeze on multi-million-node unions.
            int inserted = await Task.Run(() => _doc.Graft(other, anchor), cts.Token);
            // Visible-row state is stale: existing nodes got new children or
            // siblings. Easiest correct path is a full rebuild; for huge docs
            // we could do incremental but rebuild is bounded by visible state
            // depth, not document size.
            _tree.RefreshVisible();
            if (inserted >= 0)
                _tree.SelectId(inserted);
            SetHasDocument(true);
            UpdateElementsLabel();
            SetStatus(Localization.F("Status.Unioned", Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            SetStatus(Localization.T("Error.GraftFailed") + " " + ex.Message);
        }
    }

    #endregion

    #region Edit / Undo

    // The detail TextBox doubles as the value input during editing. We
    // capture the Enter/Escape keys only while in edit mode and remove the
    // handler when we're done, so normal preview mode keeps its plain
    // read-only behaviour.
    EventHandler<KeyEventArgs>? _detailEditKeyHandler;

    void StartEdit()
    {
        int id = _tree.SelectedId;
        if (id < 0 || !_doc.CanEditValue(id))
        {
            SetStatus(Localization.T("Error.EditNoLeaf"));
            return;
        }

        _editMode = EditPanelMode.EditValue;
        _editTargetId = id;
        var type = _doc.TypeOf(id);
        _editTitle.Text = Localization.F(
            "Edit.Title.EditValue",
            type.ToString().ToLowerInvariant()
        );

        _editTypeCombo.IsVisible = false;
        _editKey.IsVisible = false;

        SwitchDetailToEditing(RawEditableText(id, type), enabled: type != JsonNodeType.Null);

        _editPanel.IsVisible = true;
        _detail.Focus();
        _detail.SelectAll();
    }

    void StartAddChild()
    {
        int parentId = _tree.SelectedId;
        if (parentId < 0 || !_doc.CanAddChild(parentId))
        {
            SetStatus(Localization.T("Error.EditAddBranchOnly"));
            return;
        }

        _editMode = EditPanelMode.AddChild;
        _editTargetId = parentId;
        _editTitle.Text = Localization.T("Edit.Title.AddChild");

        var parentType = _doc.TypeOf(parentId);
        _editTypeCombo.IsVisible = true;
        if (_editTypeCombo.SelectedIndex < 0)
            _editTypeCombo.SelectedIndex = 2; // String
        _editKey.IsVisible = parentType == JsonNodeType.Object;
        _editKey.Text = string.Empty;
        SwitchDetailToEditing(string.Empty, enabled: true);
        UpdateEditValueEnabled();

        _editPanel.IsVisible = true;
        if (parentType == JsonNodeType.Object)
            _editKey.Focus();
        else
            _detail.Focus();
    }

    // Builds the raw editable text for a primitive id - used both to seed
    // the shared detail TextBox when entering edit mode and (indirectly) by
    // tests that snapshot the edit string before Apply.
    string RawEditableText(int id, JsonNodeType type) =>
        type switch
        {
            JsonNodeType.String => _doc.GetString(id),
            JsonNodeType.Number => _doc.GetNumber(id).ToString(CultureInfo.InvariantCulture),
            JsonNodeType.Boolean => _doc.GetBool(id) ? "true" : "false",
            _ => string.Empty,
        };

    void SwitchDetailToEditing(string text, bool enabled)
    {
        // First time: capture a handler reference so we can remove it later
        // without leaking. AcceptsReturn flips to false for single-line edit;
        // the panel's height stays consistent.
        if (_detailEditKeyHandler == null)
        {
            _detailEditKeyHandler = (s, e) => HandleEditKey(e);
            _detail.AddHandler(KeyDownEvent, _detailEditKeyHandler, RoutingStrategies.Tunnel);
        }
        // Edit mode: re-derive the raw text from the doc when in EditValue.
        // (Add mode passes "" - caller knows the placeholder/default.)
        if (_editMode == EditPanelMode.EditValue && _editTargetId >= 0)
            text = RawEditableText(_editTargetId, _doc.TypeOf(_editTargetId));
        _detail.Text = text;
        _detail.IsReadOnly = !enabled;
        _detail.AcceptsReturn = false;
        _detail.PlaceholderText = Localization.T("Edit.Placeholder.Value");
    }

    void SwitchDetailToPreview()
    {
        if (_detailEditKeyHandler != null)
        {
            _detail.RemoveHandler(KeyDownEvent, _detailEditKeyHandler);
            _detailEditKeyHandler = null;
        }
        _detail.IsReadOnly = true;
        _detail.AcceptsReturn = true;
        _detail.PlaceholderText = null;
    }

    void UpdateEditValueEnabled()
    {
        if (_editMode != EditPanelMode.AddChild)
            return;
        if (_editTypeCombo.SelectedItem is not JsonNodeType selected)
            return;
        bool primitiveWithValue =
            selected == JsonNodeType.String
            || selected == JsonNodeType.Number
            || selected == JsonNodeType.Boolean;
        _detail.IsReadOnly = !primitiveWithValue;
        if (primitiveWithValue && string.IsNullOrEmpty(_detail.Text))
        {
            _detail.Text = selected switch
            {
                JsonNodeType.Boolean => "true",
                JsonNodeType.Number => "0",
                _ => string.Empty,
            };
        }
    }

    async Task OnEditApplyAsync()
    {
        await Task.Yield(); // keep signature async for future heavy ops
        try
        {
            if (_editMode == EditPanelMode.EditValue)
                ApplyValueEdit();
            else if (_editMode == EditPanelMode.AddChild)
                ApplyAddChild();
            _tree.RefreshVisible();
            if (_editTargetId >= 0)
                _tree.SelectId(_editTargetId);
            HideEditPanel();
        }
        catch (Exception ex)
        {
            SetStatus(Localization.T("Error.EditFailed") + " " + ex.Message);
        }
    }

    void ApplyValueEdit()
    {
        int id = _editTargetId;
        var type = _doc.TypeOf(id);
        string text = _detail.Text ?? string.Empty;
        switch (type)
        {
            case JsonNodeType.String:
                _doc.SetString(id, text);
                break;
            case JsonNodeType.Number:
                _doc.SetNumber(
                    id,
                    double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
                );
                break;
            case JsonNodeType.Boolean:
                if (text.Equals("true", StringComparison.OrdinalIgnoreCase))
                    _doc.SetBool(id, true);
                else if (text.Equals("false", StringComparison.OrdinalIgnoreCase))
                    _doc.SetBool(id, false);
                else
                    throw new FormatException(Localization.T("Error.EditBoolFormat"));
                break;
            case JsonNodeType.Null:
                // No-op; null has no value to edit.
                break;
        }
    }

    void ApplyAddChild()
    {
        int parentId = _editTargetId;
        if (_editTypeCombo.SelectedItem is not JsonNodeType type)
            return;
        var parentType = _doc.TypeOf(parentId);
        string? key = parentType == JsonNodeType.Object ? _editKey.Text?.Trim() : null;
        if (parentType == JsonNodeType.Object && string.IsNullOrEmpty(key))
            throw new InvalidOperationException(Localization.T("Error.EditKeyRequired"));

        _editTargetId = _doc.AddChild(parentId, key, type, _detail.Text ?? string.Empty);
    }

    void OnEditCancel()
    {
        HideEditPanel();
    }

    void HideEditPanel()
    {
        _editMode = EditPanelMode.Hidden;
        _editPanel.IsVisible = false;
        SwitchDetailToPreview();
        // Re-paint the detail with whatever is currently selected so the
        // user lands back in preview state, not whatever raw text they typed.
        int sel = _tree.SelectedId;
        if (sel >= 0)
            RenderDetail(sel);
        else
            _detail.Text = string.Empty;
        _tree.Focus();
    }

    void DoDelete()
    {
        int id = _tree.SelectedId;
        if (id < 0 || !_doc.CanDelete(id))
        {
            SetStatus(Localization.T("Error.EditDeleteInvalid"));
            return;
        }
        try
        {
            int parent = _doc.ParentOf(id);
            _doc.DeleteNode(id);
            _tree.RefreshVisible();
            if (parent >= 0)
                _tree.SelectId(parent);
            SetStatus(Localization.T("Status.Deleted"));
        }
        catch (Exception ex)
        {
            SetStatus(Localization.T("Error.EditFailed") + " " + ex.Message);
        }
    }

    void DoUndo()
    {
        if (!_doc.CanUndo)
            return;
        _doc.Undo();
        _tree.RefreshVisible();
        SetStatus(Localization.T("Status.Undone"));
    }

    void DoRedo()
    {
        if (!_doc.CanRedo)
            return;
        _doc.Redo();
        _tree.RefreshVisible();
        SetStatus(Localization.T("Status.Redone"));
    }

    #endregion

    #region View Commands

    void DoExpandAll()
    {
        if (_doc.Count > 1000)
        {
            SetStatus("Expand all is disabled for documents with more than 1,000 elements.");
            return;
        }

        _tree.OpenAll();
    }

    #endregion

    #region Recent Files

    void RebuildRecentFilesMenu()
    {
        var items = new List<MenuItem>();
        foreach (var file in _settings.RecentFiles)
        {
            var item = new MenuItem { Header = file };
            string capture = file;
            item.Click += async (s, e) => await LoadFileAsync(capture);
            items.Add(item);
        }

        _fileOpenRecent.ItemsSource = items;
        _fileOpenRecent.IsEnabled = items.Count > 0;
    }

    #endregion

    #region Dialogs

    async Task ShowSettingsAsync()
    {
        var theme = new ComboBox { Width = 240 };
        var language = new ComboBox { Width = 240 };
        var storage = new ComboBox { Width = 240 };
        var recentFilesLabel = SettingsLabel("Settings.RecentFiles");
        var fileFilterLabel = SettingsLabel("Settings.FileFilter");
        var updatesLabel = SettingsLabel("Settings.Updates");
        var appearanceLabel = SettingsLabel("Settings.Appearance");
        var languageLabel = SettingsLabel("Settings.Language");
        var storageLabel = SettingsLabel("Settings.StringStorage");
        var labels = new[]
        {
            recentFilesLabel,
            fileFilterLabel,
            updatesLabel,
            appearanceLabel,
            languageLabel,
            storageLabel,
        };
        var recentCount = new NumericUpDown
        {
            Minimum = 3,
            Maximum = 20,
            Increment = 1,
            FormatString = "0",
            Value = _settings.RecentFileCount,
            Width = 140,
        };
        var extensionFilter = new CheckBox { IsChecked = _settings.ExtensionFilter };
        var notifyUpdates = new CheckBox { IsChecked = _settings.NotifyUpdates };
        var close = new Button
        {
            Content = Localization.T("Common.Close"),
            Width = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        bool updatingDialogLocalization = false;
        ApplySettingsChoices(theme, language, storage);

        var dialog = new Window
        {
            Title = Localization.T("Settings.Title"),
            Icon = CurrentWindowIcon(),
            RequestedThemeVariant = CurrentRequestedThemeVariant(),
            ShowInTaskbar = false,
            Width = 460,
            Height = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 10,
                Children =
                {
                    SettingsRow(recentFilesLabel, recentCount),
                    SettingsRow(fileFilterLabel, extensionFilter),
                    SettingsRow(updatesLabel, notifyUpdates),
                    SettingsRow(appearanceLabel, theme),
                    SettingsRow(storageLabel, storage),
                    SettingsRow(languageLabel, language),
                    close,
                },
            },
        };

        recentCount.ValueChanged += (s, e) =>
            _settings.RecentFileCount = Math.Max(3, (int)(recentCount.Value ?? 3));
        extensionFilter.IsCheckedChanged += (s, e) =>
            _settings.ExtensionFilter = extensionFilter.IsChecked == true;
        notifyUpdates.IsCheckedChanged += (s, e) =>
            _settings.NotifyUpdates = notifyUpdates.IsChecked == true;
        theme.SelectionChanged += (s, e) =>
        {
            if (updatingDialogLocalization)
                return;

            if (theme.SelectedItem is Choice<ColorThemePreference> choice)
            {
                _settings.ColorTheme = choice.Value;
                ApplyTheme();
                dialog.RequestedThemeVariant = CurrentRequestedThemeVariant();
                ApplyWindowIcon(dialog);
                WindowChrome.Apply(dialog, IsDarkChrome(dialog));
            }
        };
        language.SelectionChanged += (s, e) =>
        {
            if (updatingDialogLocalization)
                return;

            if (language.SelectedItem is Choice<LanguagePreference> choice)
            {
                _settings.Language = choice.Value;
                Localization.Apply(choice.Value);
                ApplyLocalization();
                ApplyDialogLocalization();
            }
        };
        storage.SelectionChanged += (s, e) =>
        {
            if (updatingDialogLocalization)
                return;

            if (storage.SelectedItem is Choice<StringStorageMode> choice)
                _settings.StringStorage = choice.Value;
        };
        close.Click += (s, e) => dialog.Close();

        WindowChrome.AttachTo(dialog, () => IsDarkChrome(dialog));
        await dialog.ShowDialog(this);
        _settings.Save();
        RebuildRecentFilesMenu();

        void ApplyDialogLocalization()
        {
            updatingDialogLocalization = true;
            try
            {
                dialog.Title = Localization.T("Settings.Title");
                close.Content = Localization.T("Common.Close");
                foreach (var label in labels)
                {
                    if (label.Tag is string key)
                        label.Text = Localization.T(key);
                }
                ApplySettingsChoices(theme, language, storage);
            }
            finally
            {
                updatingDialogLocalization = false;
            }
        }
    }

    void ApplySettingsChoices(ComboBox theme, ComboBox language, ComboBox storage)
    {
        theme.ItemsSource = s_themeOrder
            .Select(p => new Choice<ColorThemePreference>(p, Localization.ThemeName(p)))
            .ToArray();
        theme.SelectedIndex = Array.IndexOf(s_themeOrder, _settings.ColorTheme);

        var languageOrder = CurrentLanguageOrder();
        language.ItemsSource = languageOrder
            .Select(p => new Choice<LanguagePreference>(p, Localization.LanguageName(p)))
            .ToArray();
        language.SelectedIndex = Array.IndexOf(languageOrder, _settings.Language);

        storage.ItemsSource = s_storageModes
            .Select(m => new Choice<StringStorageMode>(m, Localization.StringStorageName(m)))
            .ToArray();
        storage.SelectedIndex = Array.IndexOf(s_storageModes, _settings.StringStorage);
    }

    async Task ShowAboutAsync()
    {
        var link = CreateLinkText(AppInfo.HomeUrl, AppInfo.HomeUrl);
        var close = new Button
        {
            Content = Localization.T("Common.OK"),
            Width = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var dialog = new Window
        {
            Title = Localization.T("About.Title"),
            Icon = CurrentWindowIcon(),
            RequestedThemeVariant = CurrentRequestedThemeVariant(),
            ShowInTaskbar = false,
            Width = 460,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = AppInfo.AppName,
                        FontSize = 24,
                        FontWeight = FontWeight.Bold,
                    },
                    new TextBlock
                    {
                        Text = Localization.F("About.Version", GlobalSettings.Version),
                    },
                    new TextBlock { Text = AppInfo.Copyright },
                    link,
                    close,
                },
            },
        };
        close.Click += (s, e) => dialog.Close();
        WindowChrome.AttachTo(dialog, () => IsDarkChrome(dialog));
        await dialog.ShowDialog(this);
    }

    #endregion

    #region Drag and Drop

    void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            await LoadFileAsync(path);
    }

    #endregion

    #region Status and State

    void SetTitle(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            Title = AppInfo.AppName;
            return;
        }

        string loadInfo = _lastLoadDuration.HasValue
            ? $" ({Localization.F("Title.LoadedIn", FormatDuration(_lastLoadDuration.Value))})"
            : "";
        string dirty = _doc.IsModified ? "* " : "";
        Title = $"{dirty}{fileName}{loadInfo} - {AppInfo.AppName}";
    }

    void UpdateElementsLabel()
    {
        _status.Text = _doc.Count == 0 ? "" : Localization.F("Status.Elements", _doc.Count);
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor = busy ? new Cursor(StandardCursorType.AppStarting) : Cursor.Default;
        _fileMenu.IsEnabled = !busy;
        _viewMenu.IsEnabled = !busy;
        _goMenu.IsEnabled = !busy;
        _helpMenu.IsEnabled = !busy;
        _searchButton.IsEnabled = !busy && _doc.Count > 0;
        _searchText.IsEnabled = !busy && _doc.Count > 0;
        _searchType.IsEnabled = !busy && _doc.Count > 0;
        _topButton.IsEnabled = !busy && _doc.Count > 0;
        _bottomButton.IsEnabled = !busy && _doc.Count > 0;
        _collapseButton.IsEnabled = !busy && _doc.Count > 0;
        _cancelLoadButton.IsVisible = _loadCts != null;
        _cancelLoadButton.IsEnabled = _loadCts is { IsCancellationRequested: false };
    }

    void SetHasDocument(bool hasDoc)
    {
        _welcome.IsVisible = !hasDoc;
        _fileNew.IsEnabled = hasDoc;
        _fileReload.IsEnabled = hasDoc && !string.IsNullOrEmpty(_currentFile);
        _fileSave.IsEnabled = hasDoc && _doc.IsModified && !string.IsNullOrEmpty(_currentFile);
        _fileSaveAs.IsEnabled = hasDoc;
        _fileUnion.IsEnabled =
            hasDoc && _tree.SelectedId >= 0 && _doc.CanGraftInto(_tree.SelectedId);
        _fileExportFile.IsEnabled = hasDoc && _tree.SelectedIds.Count > 0;
        _fileExportClipboard.IsEnabled = hasDoc && _tree.SelectedIds.Count > 0;
        _viewExpandAll.IsEnabled = hasDoc && _doc.Count <= 1000;
        _viewCollapseAll.IsEnabled = hasDoc;
        _goTop.IsEnabled = hasDoc;
        _goBottom.IsEnabled = hasDoc;
        _goSelection.IsEnabled = hasDoc && _tree.SelectedId >= 0;
        int sel = _tree.SelectedId;
        _editButton.IsEnabled = hasDoc && sel >= 0 && _doc.CanEditValue(sel);
        _addButton.IsEnabled = hasDoc && sel >= 0 && _doc.CanAddChild(sel);
        _deleteButton.IsEnabled = hasDoc && sel >= 0 && _doc.CanDelete(sel);
        _undoButton.IsEnabled = hasDoc && _doc.CanUndo;
        _redoButton.IsEnabled = hasDoc && _doc.CanRedo;
        SetBusy(_busy);
    }

    void SetStatus(string text)
    {
        _status.Text = text;
    }

    #endregion

    #region Theme and Icons

    void ApplyTheme()
    {
        if (Application.Current is App app)
            app.ApplyTheme(_settings.ColorTheme);
        RequestedThemeVariant = CurrentRequestedThemeVariant();
        ApplyWindowIcon(this);
        WindowChrome.Apply(this, IsDarkChrome(this));
        _tree.InvalidateVisual();
    }

    void ApplyWindowIcon(Window window)
    {
        if (!_settingsReady)
            return;
        window.Icon = CurrentWindowIcon();
    }

    WindowIcon? CurrentWindowIcon()
    {
        string resource = IsDarkChrome(this)
            ? GlobalSettings.AppIconLight
            : GlobalSettings.AppIconDark;
        return LoadWindowIcon(resource);
    }

    ThemeVariant CurrentRequestedThemeVariant()
    {
        return _settings.ColorTheme switch
        {
            ColorThemePreference.Light => ThemeVariant.Light,
            ColorThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    bool IsDarkChrome(Window window)
    {
        return _settings.ColorTheme switch
        {
            ColorThemePreference.Light => false,
            ColorThemePreference.Dark => true,
            _ => Application.Current?.ActualThemeVariant == ThemeVariant.Dark
                || window.ActualThemeVariant == ThemeVariant.Dark,
        };
    }

    static WindowIcon? LoadWindowIcon(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        return stream == null ? null : new WindowIcon(stream);
    }

    #endregion

    #region Updates

    async Task CheckUpdatesAsync(bool showResult = false)
    {
        if (showResult)
            SetStatus(Localization.T("Status.CheckingUpdates"));

        var current = GlobalSettings.Version;
        var (latest, isNewer) = await GithubUpdater.CheckAsync(current, CancellationToken.None);
        if (isNewer)
        {
            ShowUpdateAvailable();
            if (showResult)
                SetStatus(
                    string.IsNullOrEmpty(latest)
                        ? Localization.T("Status.UpdateAvailable")
                        : Localization.F("Status.UpdateAvailableVersion", latest)
                );
            return;
        }

        _updateLink.IsVisible = false;
        if (!showResult)
            return;

        SetStatus(
            string.IsNullOrEmpty(latest)
                ? Localization.T("Status.UpdateCheckFailed")
                : Localization.T("Status.NoUpdates")
        );
    }

    void ShowUpdateAvailable()
    {
        _updateLink.Text = Localization.T("Status.UpdateAvailable");
        _updateLink.IsVisible = true;
    }

    #endregion

    #region Types & Helpers

    static LanguagePreference[] CurrentLanguageOrder()
    {
        return
        [
            LanguagePreference.Auto,
            .. s_languagePreferences.OrderBy(
                Localization.LanguageName,
                StringComparer.CurrentCulture
            ),
        ];
    }

    static TextBlock SettingsLabel(string key)
    {
        return new TextBlock
        {
            Tag = key,
            Text = Localization.T(key),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    static Grid SettingsRow(TextBlock label, Control control)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("160,Auto") };
        row.Children.Add(label);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    static void SetShortcut(MenuItem item, KeyGesture gesture)
    {
        item.HotKey = gesture;
        item.InputGesture = gesture;
    }

    static TextBlock CreateLinkText(
        string text,
        string url,
        bool isVisible = true,
        Thickness? margin = null
    )
    {
        var link = new TextBlock
        {
            Text = text,
            IsVisible = isVisible,
            Margin = margin ?? new Thickness(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        link.Classes.Add("link");
        link.PointerPressed += (s, e) => OpenUrl(url);
        return link;
    }

    static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
            return $"{duration.TotalMilliseconds:N0} ms";
        if (duration.TotalMinutes < 1)
            return $"{duration.TotalSeconds:0.##} s";
        return $"{duration.Minutes}m {duration.Seconds}s";
    }

    static string FormatThousands(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

    static string FormatBytes(long bytes)
    {
        const double GiB = 1024.0 * 1024.0 * 1024.0;
        const double MiB = 1024.0 * 1024.0;
        if (bytes >= GiB)
            return $"{bytes / GiB:0.##} GiB";
        if (bytes >= MiB)
            return $"{bytes / MiB:0.#} MiB";
        return $"{bytes / 1024.0:0.#} KiB";
    }

    // Reclaim parser-side garbage (Utf8JsonReader scratch strings, the
    // BuildContext interner dictionaries, arrays that were superseded by
    // grow-resize) so the post-load working set reflects what is actually
    // retained. Without this Server GC keeps committed memory near the peak.
    static void ReleaseParserGarbage()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    sealed class ThrottledProgress(MainWindow owner, string label) : IProgress<ProgressInfo>
    {
        long _lastReportTicks;
        int _lastStep;

        public void Report(ProgressInfo info)
        {
            bool force =
                info.CurrentStep != _lastStep || info.Progress <= 0 || info.Progress >= 1.0;
            long now = Environment.TickCount64;
            if (!force && now - _lastReportTicks < ProgressUiMinIntervalMs)
                return;

            _lastReportTicks = now;
            _lastStep = info.CurrentStep;
            Dispatcher.UIThread.Post(() => owner.UpdateProgress(label, info));
        }
    }

    sealed class SearchTypeChoice(SearchType type)
    {
        public SearchType Type { get; } = type;

        public override string ToString() => Localization.SearchTypeName(Type);
    }

    sealed class Choice<T>(T value, string text)
    {
        public T Value { get; } = value;
        readonly string _text = text;

        public override string ToString() => _text;
    }

    #endregion
}
