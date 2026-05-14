using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
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

    static readonly LanguagePreference[] s_languageOrder =
    [
        LanguagePreference.Auto,
        LanguagePreference.English,
        LanguagePreference.ChineseSimplified,
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
            Width = 124,
            ItemsSource = s_searchTypes,
            SelectedIndex = 0,
        };
    readonly TextBox _searchText = new();
    readonly Button _searchButton = new() { MinWidth = 80 };
    readonly Button _topButton = new() { MinWidth = 54 };
    readonly Button _bottomButton = new() { MinWidth = 66 };
    readonly Button _collapseButton = new() { MinWidth = 78 };

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

    string? _currentFile;
    string? _currentLabel;
    TimeSpan? _lastLoadDuration;
    CancellationTokenSource? _loadCts;
    bool _settingsReady;
    bool _updatingTreeScrollBar;
    bool _busy;

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
        Grid.SetColumn(_treeScrollBar, 1);
        treeHost.Children.Add(_tree);
        treeHost.Children.Add(_treeScrollBar);

        var splitter = new GridSplitter
        {
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
        };

        _detail.Margin = new Thickness(4, 8, 8, 8);
        _detail.MinWidth = 180;
        Grid.SetColumn(treeHost, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(_detail, 2);
        main.Children.Add(treeHost);
        main.Children.Add(splitter);
        main.Children.Add(_detail);
        root.Children.Add(main);

        return root;
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
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(8, 8, 8, 0),
        };

        AddToolbarChild(toolbar, _searchType, 0);
        AddToolbarChild(toolbar, _searchText, 1);
        AddToolbarChild(toolbar, _searchButton, 2);
        AddToolbarChild(toolbar, _topButton, 3);
        AddToolbarChild(toolbar, _bottomButton, 4);
        AddToolbarChild(toolbar, _collapseButton, 5);

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
        _fileNew.Click += (s, e) => DoNew();
        _fileOpen.Click += async (s, e) => await DoOpenFileAsync();
        _fileOpenClipboard.Click += async (s, e) => await DoOpenFromClipboardAsync();
        _fileReload.Click += async (s, e) => await DoReloadAsync();
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

    public void ApplyLocalization()
    {
        _fileMenu.Header = Localization.T("Menu.File");
        _fileNew.Header = Localization.T("Menu.File.New");
        _fileOpen.Header = Localization.T("Menu.File.Open");
        _fileOpenClipboard.Header = Localization.T("Menu.File.OpenClipboard");
        _fileOpenRecent.Header = Localization.T("Menu.File.OpenRecent");
        _fileReload.Header = Localization.T("Menu.File.Reload");
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
        _topButton.Content = "Top";
        _bottomButton.Content = "Bottom";
        _collapseButton.Content = Localization.T("Menu.View.CollapseAll");
        _cancelLoadButton.Content = Localization.T("Common.Cancel");
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

        await LoadCoreAsync(
            Path.GetFileName(path),
            addRecent: true,
            path,
            (progress, ct) => _doc.LoadAsync(path, progress, ct, IsJsonlPath(path))
        );
    }

    async Task LoadBytesAsync(byte[] data, string label)
    {
        await LoadCoreAsync(
            label,
            addRecent: false,
            path: null,
            (progress, ct) => _doc.LoadAsync(data, progress, ct)
        );
    }

    async Task LoadCoreAsync(
        string label,
        bool addRecent,
        string? path,
        Func<IProgress<ProgressInfo>, CancellationToken, Task> load
    )
    {
        if (_busy)
            return;

        using var cts = new CancellationTokenSource();
        _loadCts = cts;
        SetBusy(true);
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

        try
        {
            await load(progress, cts.Token);
            stopwatch.Stop();
            _currentFile = path;
            _currentLabel = label;
            _lastLoadDuration = stopwatch.Elapsed;
            _tree.Document = _doc;
            if (_tree.FirstDocumentId >= 0)
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
            SetStatus(
                $"{_doc.Count:N0} elements loaded in {FormatDuration(stopwatch.Elapsed)} from {label}"
            );
        }
        catch (OperationCanceledException)
        {
            _doc.Reset();
            _tree.Document = null;
            _detail.Text = "";
            SetHasDocument(false);
            SetStatus(Localization.T("Loading.Canceled"));
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
            if (ReferenceEquals(_loadCts, cts))
                _loadCts = null;
            SetBusy(false);
        }
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
        if (_tree.SelectedId < 0)
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
            SetStatus("Exported selection.");
        }
        catch (Exception ex)
        {
            SetStatus(Localization.T("Error.ExportFailed") + " " + ex.Message);
        }
    }

    async Task DoExportToClipboardAsync()
    {
        if (_tree.SelectedId < 0 || Clipboard == null)
            return;

        try
        {
            byte[] data = ExtractSelectionBytes();
            await Clipboard.SetTextAsync(Encoding.UTF8.GetString(data));
            SetStatus("Copied selection.");
        }
        catch (Exception ex)
        {
            SetStatus(Localization.T("Error.ExportFailed") + " " + ex.Message);
        }
    }

    byte[] ExtractSelectionBytes()
    {
        int id = _tree.SelectedId;
        if (id < 0)
            throw new InvalidOperationException(Localization.T("Error.NoSelection"));
        var type = _doc.TypeOf(id);
        if (type != JsonNodeType.Array && type != JsonNodeType.Object)
            id = _doc.ParentOf(id);
        if (id < 0)
            id = JsonTreeDocument.RootId;
        return _doc.Extract(id);
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
        var theme = new ComboBox { Width = 180 };
        var language = new ComboBox { Width = 180 };
        var recentCount = new NumericUpDown
        {
            Minimum = 3,
            Maximum = 20,
            Increment = 1,
            FormatString = "0",
            Value = _settings.RecentFileCount,
            Width = 100,
        };
        var extensionFilter = new CheckBox { IsChecked = _settings.ExtensionFilter };
        var notifyUpdates = new CheckBox { IsChecked = _settings.NotifyUpdates };
        var close = new Button
        {
            Content = Localization.T("Common.Close"),
            Width = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        theme.ItemsSource = s_themeOrder
            .Select(p => new Choice<ColorThemePreference>(p, Localization.ThemeName(p)))
            .ToArray();
        theme.SelectedIndex = Array.IndexOf(s_themeOrder, _settings.ColorTheme);
        language.ItemsSource = s_languageOrder
            .Select(p => new Choice<LanguagePreference>(p, Localization.LanguageName(p)))
            .ToArray();
        language.SelectedIndex = Array.IndexOf(s_languageOrder, _settings.Language);

        var dialog = new Window
        {
            Title = Localization.T("Settings.Title"),
            Icon = CurrentWindowIcon(),
            RequestedThemeVariant = CurrentRequestedThemeVariant(),
            ShowInTaskbar = false,
            Width = 460,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 10,
                Children =
                {
                    SettingsRow(Localization.T("Settings.RecentFiles"), recentCount),
                    SettingsRow(Localization.T("Settings.FileFilter"), extensionFilter),
                    SettingsRow(Localization.T("Settings.Updates"), notifyUpdates),
                    SettingsRow(Localization.T("Settings.Appearance"), theme),
                    SettingsRow(Localization.T("Settings.Language"), language),
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
            if (language.SelectedItem is Choice<LanguagePreference> choice)
            {
                _settings.Language = choice.Value;
                Localization.Apply(choice.Value);
                ApplyLocalization();
            }
        };
        close.Click += (s, e) => dialog.Close();

        WindowChrome.AttachTo(dialog, () => IsDarkChrome(dialog));
        await dialog.ShowDialog(this);
        _settings.Save();
        RebuildRecentFilesMenu();
    }

    static Grid SettingsRow(string label, Control control)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("160,*") };
        row.Children.Add(
            new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }
        );
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
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
            Width = 440,
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
        Title = $"{fileName}{loadInfo} - {AppInfo.AppName}";
    }

    void UpdateElementsLabel()
    {
        _status.Text = _doc.Count == 0 ? "" : Localization.F("Status.Elements", _doc.Count);
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor = busy ? new Cursor(StandardCursorType.Wait) : Cursor.Default;
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
        _fileNew.IsEnabled = hasDoc;
        _fileReload.IsEnabled = hasDoc && !string.IsNullOrEmpty(_currentFile);
        _fileExportFile.IsEnabled = hasDoc && _tree.SelectedId >= 0;
        _fileExportClipboard.IsEnabled = hasDoc && _tree.SelectedId >= 0;
        _viewExpandAll.IsEnabled = hasDoc && _doc.Count <= 1000;
        _viewCollapseAll.IsEnabled = hasDoc;
        _goTop.IsEnabled = hasDoc;
        _goBottom.IsEnabled = hasDoc;
        _goSelection.IsEnabled = hasDoc && _tree.SelectedId >= 0;
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
