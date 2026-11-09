using System.Collections.ObjectModel;
using FluentMediaGallery.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls.Primitives;
using VirtualKey = Windows.System.VirtualKey;

namespace FluentMediaGallery.App;

public sealed partial class MainWindow : Window
{
    private const int PageSize = GalleryWindowPolicy.BatchSize;

    private readonly LibraryDatabase _database = new();
    private readonly DirectoryIndexer _indexer;
    private readonly RatingImportService _ratingImporter;
    private readonly FavoritesService _favorites;
    private readonly ObservableCollection<SourceRow> _sources = new();
    private readonly ObservableCollection<FolderNodeRow> _folderRows = new();
    private readonly HashSet<string> _expandedFolders = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedFolderPath;
    private readonly ObservableCollection<MediaRow> _rows = new();
    private readonly ObservableCollection<RiverRow> _riverRows = new();
    private int _riverGeneration;
    private double _lastRiverWidth;
    private readonly ShellThumbnailCache _thumbnailCache = new();
    private readonly List<ViewerWindow> _openViewers = new();
    private readonly DispatcherQueue _uiDispatcher;

    private CancellationTokenSource? _activeOperation;
    private MediaEntry? _selectedMedia;
    private long? _selectedSourceId;
    private int _currentPage;
    private int _windowStartOffset;
    private long _matchedCount;
    private string _sortKey = "modified-desc";
    private string _layoutKey = "square";
    private bool _applyingPreferences;
    private bool _loadingMore;
    private bool _suppressScrollPaging;
    private bool _resizingSidebar;
    private bool _updatingTimeline;
    private bool _timelineDragging;
    private double _timelineDragFraction;
    private int _timelineSeekVersion;
    private bool _pagingBoundaryArmed = true;
    private bool _lastPageWasBackward;
    private CancellationTokenSource? _dimensionProbeCancellation;

    private long _timelineMinTicks;
    private long _timelineMaxTicks;
    private readonly HashSet<ScrollViewer> _trackedGalleryScrolls = new();
    private int _queryVersion;
    private bool _isReady;
    private bool _metadataWriteConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(MainTitleDragRegion);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
        AppWindow.TitleBar.ButtonHoverBackgroundColor =
            Microsoft.UI.ColorHelper.FromArgb(255, 65, 68, 74);

        _indexer = new DirectoryIndexer(_database);
        _ratingImporter = new RatingImportService(_database, new ShellRatingReader());
        _favorites = new FavoritesService(_database, new ShellRatingReader(), new ShellRatingWriter());
        _uiDispatcher = DispatcherQueue.GetForCurrentThread();

        SourceItems.ItemsSource = _folderRows;
        MediaItems.ItemsSource = _rows;
        RiverItems.ItemsSource = _riverRows;
        ViewerFilmstrip.ItemsSource = _rows;
        LayoutRoot.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Viewer_KeyDown), true);
        Closed += (_, _) =>
        {
            _activeOperation?.Cancel();
            _dimensionProbeCancellation?.Cancel();
        };

        // Startup uses only persisted SQLite. Never enumerate folders implicitly.
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _database.InitializeAsync();
            _isReady = true;
            await ReloadSourcesAsync();
            await LoadSavedViewAsync();
            await LoadPageAsync();
            OperationStatus.Text = "Gotowe. Dodaj folder i uruchom indeksowanie, gdy zechcesz.";
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "Nie udało się otworzyć bazy: " + ex.Message;
            _isReady = false;
        }
    }

    private async Task ReloadSourcesAsync()
    {
        var sources = await _database.ListSourcesAsync();
        _sources.Clear();
        foreach (var source in sources) _sources.Add(new SourceRow(source));
        if (_selectedSourceId.HasValue && sources.All(s => s.Id != _selectedSourceId))
        {
            _selectedSourceId = null;
            _selectedFolderPath = null;
        }

        await ReloadFolderTreeAsync();
        UpdateHeader();
    }

    private async Task ReloadFolderTreeAsync()
    {
        var rows = new List<FolderNodeRow>();
        foreach (var root in _sources)
            await AppendFolderAsync(rows, root.Source.Id, root.Source.Root, 0,
                isRoot: true, root.Status);

        var shortcuts = await _database.ListShortcutsAsync();
        foreach (var shortcut in shortcuts)
            rows.Add(new FolderNodeRow(shortcut.SourceId, shortcut.FolderPath, 0,
                false, true, false, "Skrót · ten sam indeks"));

        // Keep existing containers in place. New or removed descendants receive
        // native AddDeleteThemeTransition/RepositionThemeTransition animations.
        static bool SameNode(FolderNodeRow a, FolderNodeRow b) =>
            a.SourceId == b.SourceId && a.IsRoot == b.IsRoot &&
            a.IsShortcut == b.IsShortcut && a.Depth == b.Depth &&
            string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);

        var prefix = 0;
        while (prefix < _folderRows.Count && prefix < rows.Count &&
               SameNode(_folderRows[prefix], rows[prefix]))
        {
            if (_folderRows[prefix].Chevron != rows[prefix].Chevron ||
                _folderRows[prefix].Status != rows[prefix].Status)
                _folderRows[prefix] = rows[prefix];
            prefix++;
        }
        var suffix = 0;
        while (suffix < _folderRows.Count - prefix &&
               suffix < rows.Count - prefix &&
               SameNode(_folderRows[_folderRows.Count - 1 - suffix],
                   rows[rows.Count - 1 - suffix]))
            suffix++;

        while (_folderRows.Count > prefix + suffix)
            _folderRows.RemoveAt(prefix);
        for (var i = prefix; i < rows.Count - suffix; i++)
            _folderRows.Insert(i, rows[i]);
    }

    private async Task AppendFolderAsync(List<FolderNodeRow> rows, long sourceId,
        string folder, int depth, bool isRoot, string status)
    {
        var expanded = _expandedFolders.Contains(folder);
        rows.Add(new FolderNodeRow(sourceId, folder, depth, isRoot, false, expanded,
            status));
        if (!expanded || depth >= 20) return;

        var children = await _database.GetIndexedChildFoldersAsync(sourceId, folder);
        foreach (var child in children)
            await AppendFolderAsync(rows, sourceId, child, depth + 1, false, "");
    }

    private void UpdateHeader()
    {
        if (_selectedFolderPath is not null)
        {
            HeaderTitle.Text = Path.GetFileName(_selectedFolderPath.TrimEnd('\\'));
            return;
        }
        var selected = _sources.FirstOrDefault(s => s.Source.Id == _selectedSourceId);
        HeaderTitle.Text = selected?.Title ?? "Wszystkie media";
    }

    private string CurrentViewKey()
    {
        if (_selectedFolderPath is not null)
            return "folder:" + _selectedFolderPath.ToUpperInvariant();
        if (_selectedSourceId is not null)
            return "source:" + _selectedSourceId.Value;
        return FavoritesOnly.IsChecked == true ? "favorites" : "all";
    }

    private async Task LoadSavedViewAsync()
    {
        if (!_isReady) return;
        var pref = await _database.GetViewPreferenceAsync(CurrentViewKey());
        _sortKey = pref.SortKey;
        _layoutKey = pref.LayoutKey;
        _applyingPreferences = true;
        try
        {
            UpdateSortCaption();
            ViewModeBox.SelectedItem = ViewModeBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == _layoutKey);
            ApplyLayoutMode();
        }
        finally { _applyingPreferences = false; }
    }

    private void ApplyLayoutMode()
    {
        MediaItems.Visibility = _layoutKey == "river" ? Visibility.Collapsed : Visibility.Visible;
        RiverItems.Visibility = _layoutKey == "river" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RebuildRiverAsync()
    {
        if (_layoutKey != "river") return;
        var version = ++_riverGeneration;
        var sample = _rows.ToArray();
        var width = RiverItems.ActualWidth > 300 ? RiverItems.ActualWidth
            : MediaItems.ActualWidth > 300 ? MediaItems.ActualWidth : 1050;
        // One range caption for the current filtered gallery, not a new row
        // for each month (which created isolated oversized logo tiles).
        var rangeLabel = _timelineMinTicks > 0 && _timelineMaxTicks >= _timelineMinTicks
            ? $"{new DateTime(_timelineMinTicks, DateTimeKind.Utc).ToLocalTime():d MMMM yyyy} – " +
              $"{new DateTime(_timelineMaxTicks, DateTimeKind.Utc).ToLocalTime():d MMMM yyyy}"
            : "";
        var built = await Task.Run(() => RiverLayout.BuildWithDimensions(sample, width, rangeLabel));
        if (version != _riverGeneration || _layoutKey != "river") return;
        var rows = built.Rows;
        // Preserve completed row visuals, thumbnails and current scroll position.
        // Only a changed final row (or the tail after a resize) is replaced.
        var common = 0;
        while (common < _riverRows.Count && common < rows.Count &&
               SameRiverRow(_riverRows[common], rows[common]))
            common++;
        while (_riverRows.Count > common) _riverRows.RemoveAt(_riverRows.Count - 1);
        for (var i = common; i < rows.Count; i++) _riverRows.Add(rows[i]);

        // Save discovered aspect ratios for subsequent launches and repeated
        // gallery sessions. A failed cache write must not block photo browsing.
        if (built.DimensionsToCache.Count != 0)
        {
            try
            {
                await _database.CacheImageDimensionsAsync(built.DimensionsToCache);
                if (version != _riverGeneration) return;

                // Also refresh in-memory records. Otherwise a resize would
                // rewrite the same cached dimensions again and again until
                // the next SQLite page reload.
                var byId = built.DimensionsToCache.ToDictionary(d => d.Id);
                foreach (var row in _rows)
                {
                    if (!byId.TryGetValue(row.Entry.Id, out var d) ||
                        row.Entry.LastWriteUtcTicks != d.ModifiedUtcTicks ||
                        row.Entry.Size != d.Size) continue;
                    row.UpdateEntry(row.Entry with
                    {
                        PixelWidth = d.Width,
                        PixelHeight = d.Height
                    });
                }
            }
            catch (Exception ex)
            {
                OperationStatus.Text = "Cache wymiarów zdjęć: " + ex.Message;
            }
        }
    }

    private void ScheduleDimensionProbe()
    {
        _dimensionProbeCancellation?.Cancel();
        _dimensionProbeCancellation = new CancellationTokenSource();
        var token = _dimensionProbeCancellation.Token;
        var generation = _queryVersion;
        var visible = _rows.Where(row => row.Entry.Kind == MediaKind.Photo &&
            (row.Entry.PixelWidth <= 0 || row.Entry.PixelHeight <= 0)).ToArray();
        if (visible.Length == 0) return;

        // Do not await this work from painting, scrolling, or timeline handlers.
        // Offline SMB reads may take seconds; gray placeholders remain responsive.
        _ = ProbeAsync();
        async Task ProbeAsync()
        {
            try
            {
                var found = await Task.Run(() =>
                    RiverLayout.ProbeMissingDimensions(visible, token), token);
                token.ThrowIfCancellationRequested();
                if (found.Count == 0 || generation != _queryVersion) return;
                await _database.CacheImageDimensionsAsync(found, token);
                token.ThrowIfCancellationRequested();
                if (generation != _queryVersion) return;

                var measured = found.ToDictionary(d => d.Id);
                foreach (var row in _rows)
                {
                    if (!measured.TryGetValue(row.Entry.Id, out var d) ||
                        row.Entry.LastWriteUtcTicks != d.ModifiedUtcTicks ||
                        row.Entry.Size != d.Size) continue;
                    row.UpdateEntry(row.Entry with
                    {
                        PixelWidth = d.Width, PixelHeight = d.Height
                    });
                }
                if (_layoutKey == "river" && !_loadingMore && !_timelineDragging)
                    await RebuildRiverAsync();
            }
            catch (OperationCanceledException) { /* User moved to another page. */ }
            catch (Exception ex)
            {
                if (generation == _queryVersion)
                    OperationStatus.Text = "Wymiary miniatur: " + ex.Message;
            }
        }
    }

    private static bool SameRiverRow(RiverRow a, RiverRow b)
    {
        if (a.Header != b.Header || a.Tiles.Count != b.Tiles.Count ||
            a.CanvasHeight != b.CanvasHeight || a.CanvasWidth != b.CanvasWidth)
            return false;
        for (var i = 0; i < a.Tiles.Count; i++)
        {
            // An identical media ID is not enough: after a view reload the
            // previous MediaRow (and its thumbnail work) is canceled. Reuse
            // rows only when they still reference the SAME live MediaRow.
            if (!ReferenceEquals(a.Tiles[i].Media, b.Tiles[i].Media) ||
                a.Tiles[i].Left != b.Tiles[i].Left ||
                a.Tiles[i].Top != b.Tiles[i].Top ||
                a.Tiles[i].ImageWidth != b.Tiles[i].ImageWidth ||
                a.Tiles[i].ImageHeight != b.Tiles[i].ImageHeight)
                return false;
        }
        return true;
    }

    private async void RiverItems_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isReady || _layoutKey != "river" ||
            Math.Abs(e.NewSize.Width - _lastRiverWidth) < 24) return;
        _lastRiverWidth = e.NewSize.Width;
        await RebuildRiverAsync();
    }

    private void RiverItems_ContainerContentChanging(ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not RiverRow row) return;
        foreach (var tile in row.Tiles)
            tile.Media.EnsureThumbnail(_thumbnailCache, _uiDispatcher);
    }

    private async void RiverTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MediaRow row })
            await OpenMediaAsync(row);
    }

    private async Task LoadPageAsync(int? requestedOffset = null)
    {
        if (!_isReady) return;
        var version = ++_queryVersion;
        _dimensionProbeCancellation?.Cancel();
        _pagingBoundaryArmed = false;
        if (requestedOffset.HasValue)
            GalleryLoadingOverlay.Visibility = Visibility.Visible;
        try
        {
            var search = SearchBox.Text.Trim();
            var favorites = FavoritesOnly.IsChecked == true;
            var count = await _database.CountFilteredAsync(_selectedSourceId, favorites, search,
                folderPath: _selectedFolderPath);
            var nextOffset = Math.Min(requestedOffset.GetValueOrDefault(), Math.Max(0, (int)count - 1));
            nextOffset = Math.Max(0, (nextOffset / PageSize) * PageSize);
            var items = await _database.GetPageAsync(nextOffset, PageSize,
                favorites, search, sourceId: _selectedSourceId,
                folderPath: _selectedFolderPath, sortKey: _sortKey);
            if (version != _queryVersion) return;

            _matchedCount = count;
            var date = await _database.GetDateRangeAsync(_selectedSourceId, favorites,
                search, _selectedFolderPath, _sortKey.StartsWith("created", StringComparison.Ordinal));
            if (version != _queryVersion) return;
            _timelineMinTicks = date.Minimum;
            _timelineMaxTicks = date.Maximum;
            _windowStartOffset = nextOffset;
            _currentPage = nextOffset / PageSize;
            _suppressScrollPaging = true;
            foreach (var staleRow in _rows) staleRow.ReleaseThumbnail();
            _rows.Clear();
            foreach (var item in items) _rows.Add(new MediaRow(item));
            UpdateGalleryProgress();
            ApplyLayoutMode();
            await RebuildRiverAsync();
            if (version != _queryVersion) return;
            GalleryLoadingOverlay.Visibility = Visibility.Collapsed;
            ResetGalleryScroll();
            ScheduleDimensionProbe();
        }
        catch (Exception ex)
        {
            if (version == _queryVersion)
            {
                _suppressScrollPaging = false;
                GalleryLoadingOverlay.Visibility = Visibility.Collapsed;
                OperationStatus.Text = "Błąd odczytu galerii: " + ex.Message;
            }
        }
    }

    private void UpdateGalleryProgress()
    {
        var from = _rows.Count == 0 ? 0 : _windowStartOffset + 1;
        var to = _windowStartOffset + _rows.Count;
        PageLabel.Text = $"W pamięci {from:N0}–{to:N0} z {_matchedCount:N0} " +
            $"(maks. {GalleryWindowPolicy.MaxRetainedMedia}). Przewijaj w obu kierunkach.";
        LibrarySummary.Text = $"{_matchedCount:N0} mediów • {_sortKey} • {_layoutKey} • SQLite";
        RenderTimelineMarkers();
        if (!_timelineDragging) UpdateTimelinePosition();
    }

    private bool TimelineUsesDate =>
        _sortKey.StartsWith("modified", StringComparison.Ordinal) ||
        _sortKey.StartsWith("created", StringComparison.Ordinal);

    private double DateFraction(long ticks)
    {
        if (_timelineMaxTicks <= _timelineMinTicks) return 0;
        var ratio = Math.Clamp((ticks - (double)_timelineMinTicks) /
            (_timelineMaxTicks - (double)_timelineMinTicks), 0, 1);
        return _sortKey.EndsWith("-asc", StringComparison.Ordinal) ? ratio : 1 - ratio;
    }

    private long DateForFraction(double fraction)
    {
        var ratio = _sortKey.EndsWith("-asc", StringComparison.Ordinal)
            ? fraction : 1 - fraction;
        return _timelineMinTicks + (long)((_timelineMaxTicks - _timelineMinTicks) *
            Math.Clamp(ratio, 0d, 1d));
    }

    private string TimelineLabel(double fraction)
    {
        if (TimelineUsesDate && _timelineMaxTicks > _timelineMinTicks)
        {
            var ticks = Math.Clamp(DateForFraction(fraction), DateTime.MinValue.Ticks,
                DateTime.MaxValue.Ticks);
            return new DateTime(ticks, DateTimeKind.Utc).ToLocalTime()
                .ToString("MMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("pl-PL"));
        }
        if (_sortKey.StartsWith("rating", StringComparison.Ordinal))
        {
            var stars = Math.Clamp((int)Math.Round(fraction * 5), 0, 5);
            return (_sortKey.EndsWith("-asc", StringComparison.Ordinal) ? stars : 5 - stars) + "★";
        }
        if (_sortKey.StartsWith("favorites", StringComparison.Ordinal))
            return fraction >= 0.5 ? "—" : "♥";
        return fraction >= 0.5 ? "A–Z" : "Z–A";
    }

    private void UpdateTimelinePosition()
    {
        if (_timelineDragging || !_isReady) return;
        var fraction = 0d;
        if (_rows.Count > 0)
        {
            var itemIndex = EstimateVisibleMediaIndex();
            var visibleIndex = Math.Clamp(itemIndex, 0, _rows.Count - 1);
            if (TimelineUsesDate && _timelineMaxTicks > _timelineMinTicks)
            {
                var entry = _rows[visibleIndex].Entry;
                var ticks = _sortKey.StartsWith("created", StringComparison.Ordinal)
                    && entry.CreatedUtcTicks > 0 ? entry.CreatedUtcTicks
                    : entry.LastWriteUtcTicks;
                fraction = DateFraction(ticks);
            }
            else if (_matchedCount > 1)
                fraction = Math.Clamp((_windowStartOffset + (double)visibleIndex) /
                    (_matchedCount - 1), 0, 1);
        }
        MoveTimelineThumb(fraction);
    }

    private int EstimateVisibleMediaIndex()
    {
        var scroll = CurrentGalleryScroll();
        if (scroll is null) return 0;
        var pixels = Math.Max(0, scroll.VerticalOffset);
        if (_layoutKey != "river")
        {
            var wrap = MediaItems.ItemsPanelRoot as ItemsWrapGrid;
            var width = wrap?.ItemWidth > 0 ? wrap.ItemWidth : 184d;
            var columns = Math.Max(1, (int)Math.Floor(
                Math.Max(width, MediaItems.ActualWidth - 16) / width));
            var height = wrap?.ItemHeight > 0 ? wrap.ItemHeight : 202d;
            return Math.Min(_rows.Count - 1,
                (int)(pixels / height) * columns);
        }
        var index = 0;
        foreach (var band in _riverRows)
        {
            var h = band.CanvasHeight + 4 +
                (string.IsNullOrWhiteSpace(band.Header) ? 0 : 26);
            if (pixels < h) break;
            pixels -= h;
            index += band.Tiles.Count;
        }
        return Math.Clamp(index, 0, Math.Max(0, _rows.Count - 1));
    }

    private void MoveTimelineThumb(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        var height = Math.Max(1d, TimelineTrack.ActualHeight - 24);
        var y = 12 + height * fraction;
        Canvas.SetTop(TimelineThumb, y);
        Canvas.SetTop(TimelineBubble, Math.Clamp(y - 14, 0,
            Math.Max(0, TimelineTrack.ActualHeight - 30)));
        TimelineValue.Text = TimelineLabel(fraction);
    }

    private void RenderTimelineMarkers()
    {
        if (TimelineTrack.ActualHeight < 80) return;
        TimelineMarks.Children.Clear();
        void Mark(double fraction, string label, bool big)
        {
            var top = 12 + Math.Clamp(fraction, 0, 1) *
                Math.Max(1d, TimelineTrack.ActualHeight - 24);
            var dash = new Border
            {
                Width = big ? 9 : 4,
                Height = big ? 2 : 1,
                Background = new SolidColorBrush(ColorHelper.FromArgb(
                    255, big ? (byte)156 : (byte)105,
                    big ? (byte)161 : (byte)110,
                    big ? (byte)167 : (byte)116)),
                CornerRadius = new CornerRadius(1)
            };
            Canvas.SetLeft(dash, 39);
            Canvas.SetTop(dash, top);
            TimelineMarks.Children.Add(dash);
            if (!big) return;
            var text = new TextBlock
            {
                Text = label, FontSize = 10,
                Foreground = new SolidColorBrush(Colors.LightGray),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(text, 18);
            Canvas.SetTop(text, Math.Clamp(top - 17, 0,
                Math.Max(0, TimelineTrack.ActualHeight - 20)));
            TimelineMarks.Children.Add(text);
        }

        if (TimelineUsesDate && _timelineMaxTicks > _timelineMinTicks)
        {
            var firstYear = new DateTime(_timelineMinTicks, DateTimeKind.Utc).Year;
            var lastYear = new DateTime(_timelineMaxTicks, DateTimeKind.Utc).Year;
            var years = Math.Clamp(lastYear - firstYear + 1, 1, 150);
            var step = Math.Max(1, (int)Math.Ceiling(years * 35d /
                Math.Max(1, TimelineTrack.ActualHeight - 24)));
            for (var year = firstYear; year <= lastYear; year++)
            {
                var ticks = new DateTime(year, 1, 1, 0, 0, 0,
                    DateTimeKind.Utc).Ticks;
                var isMajor = year == firstYear || year == lastYear ||
                    (year - firstYear) % step == 0;
                Mark(DateFraction(ticks), year.ToString(), isMajor);
            }
        }
        else
        {
            for (var i = 0; i <= 5; i++)
                Mark(i / 5d, _sortKey.StartsWith("rating", StringComparison.Ordinal)
                    ? (_sortKey.EndsWith("-asc", StringComparison.Ordinal)
                        ? i : 5 - i) + "★"
                    : i == 0 ? "A" : i == 5 ? "Z" : "", i == 0 || i == 5 ||
                        _sortKey.StartsWith("rating", StringComparison.Ordinal));
        }
    }

    private ScrollViewer? CurrentGalleryScroll()
        => FindScrollViewer(_layoutKey == "river" ? RiverItems : MediaItems);

    private void ResetGalleryScroll()
    {
        // A new search, sort, folder or timeline seek has a new absolute origin.
        _uiDispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            CurrentGalleryScroll()?.ChangeView(null, 0, null, true);
            _suppressScrollPaging = false;
        });
    }

    private void RestoreGalleryScroll(double targetOffset, int version)
    {
        _suppressScrollPaging = true;
        // Defer until the virtualized ItemsPanel has measured inserted/removed rows.
        _uiDispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (version == _queryVersion)
            {
                var scroll = CurrentGalleryScroll();
                if (scroll is not null)
                {
                    scroll.UpdateLayout();
                    scroll.ChangeView(null,
                        Math.Clamp(targetOffset, 0, scroll.ScrollableHeight), null, true);
                }
            }
            _suppressScrollPaging = false;
        });
    }

    private double ApproximateTopHeight(int count)
    {
        if (count <= 0) return 0;
        if (_layoutKey != "river")
        {
            var grid = MediaItems.ItemsPanelRoot as ItemsWrapGrid;
            var itemWidth = grid?.ItemWidth > 0 ? grid.ItemWidth : 184d;
            var itemHeight = grid?.ItemHeight > 0 ? grid.ItemHeight : 202d;
            var columns = Math.Max(1, (int)Math.Floor(
                Math.Max(itemWidth, MediaItems.ActualWidth - 16) / itemWidth));
            return Math.Ceiling(count / (double)columns) * itemHeight;
        }

        double pixels = 0;
        var remaining = count;
        foreach (var row in _riverRows)
        {
            if (remaining <= 0) break;
            if (row.Tiles.Count == 0) continue;
            var fraction = Math.Min(remaining, row.Tiles.Count) /
                           (double)row.Tiles.Count;
            pixels += fraction * (row.CanvasHeight + 4 +
                (string.IsNullOrEmpty(row.Header) ? 0 : 26));
            remaining -= Math.Min(remaining, row.Tiles.Count);
        }
        return pixels;
    }

    private async Task AppendMoreAsync()
    {
        if (_loadingMore || !_isReady) return;
        var next = _windowStartOffset + _rows.Count;
        if (next >= _matchedCount) return;

        var version = _queryVersion;
        _loadingMore = true;
        try
        {
            var scroll = CurrentGalleryScroll();
            var oldOffset = scroll?.VerticalOffset ?? 0;
            var items = await _database.GetPageAsync(next, PageSize,
                FavoritesOnly.IsChecked == true, SearchBox.Text.Trim(),
                sourceId: _selectedSourceId, folderPath: _selectedFolderPath,
                sortKey: _sortKey);
            if (version != _queryVersion || items.Count == 0) return;

            var prune = Math.Min(_rows.Count,
                GalleryWindowPolicy.TrimExcess(_rows.Count, items.Count));
            var removedHeight = ApproximateTopHeight(prune);
            foreach (var item in items) _rows.Add(new MediaRow(item));
            for (var i = 0; i < prune; i++)
            {
                var evicted = _rows[0];
                _rows.RemoveAt(0);
                evicted.ReleaseThumbnail();
            }
            _windowStartOffset += prune;

            await RebuildRiverAsync();
            if (version != _queryVersion) return;
            UpdateGalleryProgress();
            ScheduleDimensionProbe();
            if (prune > 0)
                RestoreGalleryScroll(oldOffset - removedHeight, version);
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "Błąd wczytywania kolejnych mediów: " + ex.Message;
        }
        finally { _loadingMore = false; }
    }

    private async Task PrependEarlierAsync()
    {
        if (_loadingMore || !_isReady || _windowStartOffset <= 0) return;

        var version = _queryVersion;
        var previous = GalleryWindowPolicy.PreviousBatchOffset(_windowStartOffset);
        var count = _windowStartOffset - previous;
        if (count == 0) return;

        _loadingMore = true;
        try
        {
            var scroll = CurrentGalleryScroll();
            var oldOffset = scroll?.VerticalOffset ?? 0;
            var items = await _database.GetPageAsync(previous, count,
                FavoritesOnly.IsChecked == true, SearchBox.Text.Trim(),
                sourceId: _selectedSourceId, folderPath: _selectedFolderPath,
                sortKey: _sortKey);
            if (version != _queryVersion || items.Count == 0) return;

            var prune = Math.Min(_rows.Count,
                GalleryWindowPolicy.TrimExcess(_rows.Count, items.Count));
            for (var i = items.Count - 1; i >= 0; i--)
                _rows.Insert(0, new MediaRow(items[i]));
            _windowStartOffset = previous;
            for (var i = 0; i < prune; i++)
            {
                var index = _rows.Count - 1;
                var evicted = _rows[index];
                _rows.RemoveAt(index);
                evicted.ReleaseThumbnail();
            }

            await RebuildRiverAsync();
            if (version != _queryVersion) return;
            var insertedHeight = ApproximateTopHeight(items.Count);
            UpdateGalleryProgress();
            ScheduleDimensionProbe();
            RestoreGalleryScroll(oldOffset + insertedHeight, version);
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "Błąd wczytywania poprzednich mediów: " + ex.Message;
        }
        finally { _loadingMore = false; }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (result is not null) return result;
        }
        return null;
    }

    private void MediaItems_Loaded(object sender, RoutedEventArgs e)
    {
        TrackScroll(sender as DependencyObject);
        UpdateSquareTileWidth(MediaItems.ActualWidth);
    }

    private void MediaItems_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateSquareTileWidth(e.NewSize.Width);

    private void UpdateSquareTileWidth(double width)
    {
        if (MediaItems.ItemsPanelRoot is not ItemsWrapGrid grid || width < 180) return;
        var available = Math.Max(180, width - 16);
        var count = Math.Max(1, (int)Math.Floor(available / 186));
        grid.ItemWidth = Math.Floor(available / count);
    }
    private void RiverItems_Loaded(object sender, RoutedEventArgs e)
        => TrackScroll(sender as DependencyObject);

    private void TrackScroll(DependencyObject? root)
    {
        if (root is null) return;
        var scroll = FindScrollViewer(root);
        if (scroll is null || !_trackedGalleryScrolls.Add(scroll)) return;
        scroll.ViewChanged += GalleryScroll_ViewChanged;
    }

    private async void GalleryScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || _loadingMore ||
            _suppressScrollPaging || _timelineDragging) return;

        UpdateTimelinePosition();

        // Intermediate notifications fire repeatedly while a user drags a
        // thumb. Reacting to them while replacing mosaic bands previously
        // caused the scrollbar to bounce between the top and the middle.
        if (e.IsIntermediate) return;

        var nearTop = scroll.VerticalOffset < Math.Min(130, scroll.ViewportHeight * 0.25);
        var nearBottom = scroll.VerticalOffset + scroll.ViewportHeight >=
            scroll.ExtentHeight - scroll.ViewportHeight * 0.8 &&
            scroll.ExtentHeight > scroll.ViewportHeight;

        if (!nearTop && !nearBottom)
        {
            _pagingBoundaryArmed = true;
            return;
        }
        if (!_pagingBoundaryArmed) return;
        _pagingBoundaryArmed = false;
        if (nearTop && _windowStartOffset > 0)
            await PrependEarlierAsync();
        else if (nearBottom)
            await AppendMoreAsync();
    }

    private void UpdateSortCaption()
    {
        var last = _sortKey.LastIndexOf('-');
        var field = last < 0 ? "modified" : _sortKey[..last];
        var direction = _sortKey.EndsWith("-asc", StringComparison.Ordinal) ? "↑" : "↓";
        var label = field switch
        {
            "created" => "Utworzenie",
            "name" => "Nazwa",
            "rating" => "Klasyfikacja",
            "favorites" => "Ulubione",
            _ => "Modyfikacja"
        };
        SortButton.Content = $"{label} {direction}";
    }

    private async Task ApplySortAsync(string field, string direction)
    {
        if (!_isReady) return;
        _sortKey = field + "-" + direction;
        UpdateSortCaption();
        await _database.SetViewPreferenceAsync(CurrentViewKey(), _sortKey, _layoutKey);
        await LoadPageAsync();
    }

    private async void SortField_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingPreferences || sender is not MenuFlyoutItem chosen) return;
        var field = chosen.Tag?.ToString() ?? "modified";
        var direction = _sortKey.EndsWith("-asc", StringComparison.Ordinal) ? "asc" : "desc";
        await ApplySortAsync(field, direction);
    }

    private async void SortDirection_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingPreferences || sender is not MenuFlyoutItem chosen) return;
        var direction = chosen.Tag?.ToString() ?? "desc";
        var last = _sortKey.LastIndexOf('-');
        var field = last >= 0 ? _sortKey[..last] : "modified";
        await ApplySortAsync(field, direction);
    }

    private void TitleFavorites_Click(object sender, RoutedEventArgs e)
        => FavoritesOnly.IsChecked = FavoritesOnly.IsChecked != true;

    private async void ViewModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isReady || _applyingPreferences ||
            ViewModeBox.SelectedItem is not ComboBoxItem chosen) return;
        _layoutKey = chosen.Tag?.ToString() ?? "square";
        ApplyLayoutMode();
        await _database.SetViewPreferenceAsync(CurrentViewKey(), _sortKey, _layoutKey);
        await RebuildRiverAsync();
    }

    private double TimelineFractionFromPointer(PointerRoutedEventArgs e)
    {
        var y = e.GetCurrentPoint(TimelineTrack).Position.Y;
        return Math.Clamp((y - 12) / Math.Max(1, TimelineTrack.ActualHeight - 24), 0, 1);
    }

    private void TimelineTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isReady) return;
        RenderTimelineMarkers();
        UpdateTimelinePosition();
    }

    private void TimelineTrack_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isReady || _matchedCount <= 0) return;
        _timelineDragging = TimelineTrack.CapturePointer(e.Pointer);
        if (!_timelineDragging) return;
        _timelineDragFraction = TimelineFractionFromPointer(e);
        TimelineBubble.Visibility = Visibility.Visible;
        MoveTimelineThumb(_timelineDragFraction);
        e.Handled = true;
    }

    private void TimelineTrack_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_timelineDragging) return;
        _timelineDragFraction = TimelineFractionFromPointer(e);
        MoveTimelineThumb(_timelineDragFraction);
        e.Handled = true;
    }

    private async void TimelineTrack_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_timelineDragging) return;
        _timelineDragFraction = TimelineFractionFromPointer(e);
        _timelineDragging = false;
        TimelineTrack.ReleasePointerCapture(e.Pointer);
        MoveTimelineThumb(_timelineDragFraction);
        TimelineBubble.Visibility = Visibility.Collapsed;
        e.Handled = true;
        await SeekTimelineAsync(_timelineDragFraction);
    }

    private void TimelineTrack_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _timelineDragging = false;
        TimelineBubble.Visibility = Visibility.Collapsed;
        UpdateTimelinePosition();
    }

    private async Task SeekTimelineAsync(double fraction)
    {
        if (!_isReady || _matchedCount <= 0) return;
        var ticket = ++_timelineSeekVersion;
        _suppressScrollPaging = true;
        _pagingBoundaryArmed = false;
        GalleryLoadingOverlay.Visibility = Visibility.Visible;
        try
        {
            long target;
            if (TimelineUsesDate && _timelineMaxTicks > _timelineMinTicks)
            {
                var date = DateForFraction(fraction);
                target = await _database.GetOffsetForDateAsync(date,
                    ascending: _sortKey.EndsWith("-asc", StringComparison.Ordinal),
                    creationDate: _sortKey.StartsWith("created", StringComparison.Ordinal),
                    sourceId: _selectedSourceId,
                    favoritesOnly: FavoritesOnly.IsChecked == true,
                    search: SearchBox.Text.Trim(), folderPath: _selectedFolderPath);
            }
            else target = (long)Math.Round((_matchedCount - 1) * fraction);

            if (ticket != _timelineSeekVersion) return;
            await LoadPageAsync((int)Math.Min(int.MaxValue,
                Math.Clamp(target, 0, _matchedCount - 1)));
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "Błąd przejścia do daty: " + ex.Message;
            GalleryLoadingOverlay.Visibility = Visibility.Collapsed;
            _suppressScrollPaging = false;
        }
    }

    private void SidebarGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _resizingSidebar = true;
        SidebarGrip.CapturePointer(e.Pointer);
    }

    private void SidebarGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizingSidebar) return;
        var x = e.GetCurrentPoint(LayoutRoot).Position.X;
        SidebarColumn.Width = new GridLength(Math.Clamp(x, 200, 500));
    }

    private void SidebarGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _resizingSidebar = false;
        SidebarGrip.ReleasePointerCapture(e.Pointer);
    }

    private async Task RegisterLocationAsync(string path)
    {
        var registered = await _database.RegisterLocationAsync(path);
        _selectedSourceId = registered.SourceId;
        _selectedFolderPath = registered.IsShortcut ? registered.FolderPath : null;
        _currentPage = 0;
        SourcePath.Text = "";
        await ReloadSourcesAsync();
        await LoadSavedViewAsync();
        await LoadPageAsync();
        OperationStatus.Text = registered.IsShortcut
            ? "Dodano skrót podfolderu bez ponownego indeksowania. Rozwiń źródło po lewej stronie."
            : registered.IsNew
                ? "Folder dodany. Uruchom indeksowanie wybranego źródła."
                : "Ten folder jest już w bibliotece.";
    }

    private async void AddSource_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady || _activeOperation is not null) return;
        try
        {
            await RegisterLocationAsync(SourcePath.Text.Trim());
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "Błąd dodawania lokalizacji: " + ex.Message;
        }
    }

    private async void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady || _activeOperation is not null) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            await RegisterLocationAsync(folder.Path);
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "Nie udało się wybrać folderu: " + ex.Message;
        }
    }

    private async void FolderToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FolderNodeRow folder } || folder.IsShortcut)
            return;
        if (!_expandedFolders.Add(folder.Path))
            _expandedFolders.Remove(folder.Path);
        await ReloadFolderTreeAsync();
    }

    private async void SidebarFavorites_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        _selectedSourceId = null;
        _selectedFolderPath = null;
        _currentPage = 0;
        UpdateHeader();
        if (FavoritesOnly.IsChecked != true)
        {
            FavoritesOnly.IsChecked = true; // existing handler loads the view
            return;
        }
        await LoadSavedViewAsync();
        await LoadPageAsync();
    }

    private async void AllSources_Click(object sender, RoutedEventArgs e)
    {
        _selectedSourceId = null;
        _selectedFolderPath = null;
        _currentPage = 0;
        UpdateHeader();
        if (FavoritesOnly.IsChecked == true)
        {
            FavoritesOnly.IsChecked = false; // existing handler reloads the gallery
            return;
        }
        await LoadSavedViewAsync();
        await LoadPageAsync();
    }

    private async void SourceItems_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FolderNodeRow row) return;
        _selectedSourceId = row.SourceId;
        _selectedFolderPath = row.IsRoot ? null : row.Path;
        _currentPage = 0;
        UpdateHeader();
        await LoadSavedViewAsync();
        await LoadPageAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        await ReloadSourcesAsync();
        await LoadPageAsync();
        OperationStatus.Text = "Widok odświeżony z indeksu SQLite; foldery nie były skanowane.";
    }

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        _currentPage = 0;
        await LoadPageAsync();
    }

    private async void FavoritesOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        _currentPage = 0;
        await LoadSavedViewAsync();
        await LoadPageAsync();
    }

    private void MediaItems_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not MediaRow row) return;
        row.EnsureThumbnail(_thumbnailCache, _uiDispatcher);
    }

    private async void MediaItems_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MediaRow row) await OpenMediaAsync(row);
    }

    private async Task OpenMediaAsync(MediaRow row)
    {
        _selectedMedia = row.Entry;
        DetailsName.Text = row.Entry.Name;
        DetailsPath.Text = row.Entry.FullPath;
        DetailsRating.Text = row.Entry.FileRating is int rating
            ? $"System.Rating: {rating} / 99"
            : "System.Rating: nieodczytany / niedostępny";
        FavoriteButton.Content = row.Entry.IsFavorite ? "Usuń z ulubionych" : "Dodaj do ulubionych";
        FavoriteButton.IsEnabled = true;
        var state = await _database.GetFavoriteWriteStateAsync(row.Entry.Id);
        FavoriteStatus.Text = state.Pending
            ? "Zapis do pliku oczekuje: " + (state.Error ?? "niezsynchronizowany")
            : "";

        // Viewer captures filter, folder and sort context at the moment of opening.
        // It owns an independent Window and can navigate without mutating the gallery UI.
        int index = _rows.ToList().FindIndex(r => r.Entry.Id == row.Entry.Id);
        if (index < 0) return;
        var context = new GalleryViewContext(
            _selectedSourceId, _selectedFolderPath,
            FavoritesOnly.IsChecked == true, SearchBox.Text.Trim(), _sortKey);
        var viewer = new ViewerWindow(_database, _favorites, _thumbnailCache,
            context, () => _ = LoadPageAsync());
        _openViewers.Add(viewer);
        viewer.Closed += (_, _) => _openViewers.Remove(viewer);
        viewer.Activate();
        await viewer.OpenAtAsync(_windowStartOffset + (long)index);
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e)
        => await ToggleFavoriteAsync();

    private async Task ToggleFavoriteAsync()
    {
        if (_selectedMedia is not MediaEntry media || _activeOperation is not null) return;
        if (!_metadataWriteConfirmed)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = LayoutRoot.XamlRoot,
                Title = "Zapis klasyfikacji w pliku",
                Content = "Ta operacja zapisze System.Rating w oryginalnym pliku, jeżeli format na to pozwala. " +
                          "Pierwsze testy wykonuj wyłącznie na KOPIACH mediów. Kontynuować?",
                PrimaryButtonText = "Zapisz klasyfikację",
                CloseButtonText = "Anuluj",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            _metadataWriteConfirmed = true;
        }

        FavoriteButton.IsEnabled = false;
        ViewerFavoriteButton.IsEnabled = false;
        try
        {
            var desired = !media.IsFavorite;
            var result = await Task.Run(() => _favorites.SetFavoriteAsync(media.Id, desired));
            _selectedMedia = await _database.GetByIdAsync(media.Id);
            FavoriteButton.Content = desired ? "Usuń z ulubionych" : "Dodaj do ulubionych";
            FavoriteStatus.Text = result.StoredInFile
                ? "Zapisano i zweryfikowano ocenę w pliku."
                : "Ulubione zapisano tylko w SQLite. Plik: " + result.Error;
            await LoadPageAsync();

            if (_selectedMedia is not null)
                UpdateViewerAfterFavorite(_selectedMedia, FavoriteStatus.Text);
        }
        catch (Exception ex)
        {
            FavoriteStatus.Text = "Błąd zmiany ulubionych: " + ex.Message;
            if (_viewerVisible) ViewerStatus.Text = FavoriteStatus.Text;
        }
        finally
        {
            FavoriteButton.IsEnabled = _selectedMedia is not null;
            ViewerFavoriteButton.IsEnabled = _selectedMedia is not null;
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady || _activeOperation is not null) return;
        var source = _sources.FirstOrDefault(s => s.Source.Id == _selectedSourceId)?.Source;
        if (source is null)
        {
            OperationStatus.Text = "Najpierw wybierz jeden z folderów po lewej stronie.";
            return;
        }

        using var cts = new CancellationTokenSource();
        SetOperation(cts, true);
        OperationStatus.Text = "Trwa indeksowanie: " + source.Root;
        try
        {
            var progress = new Progress<int>(n =>
                _uiDispatcher.TryEnqueue(() => OperationStatus.Text = $"Indeksowanie: {n:N0} plików · {source.Root}"));
            var result = await Task.Run(() => _indexer.ScanAsync(source, progress, cts.Token), cts.Token);
            OperationStatus.Text = result.Completed
                ? $"Zindeksowano {result.Discovered:N0} plików. Trwa odczyt klasyfikacji..."
                : $"Niepełny skan / niedostępne źródło. Zachowano indeks. Błędy: {result.Errors}.";
            await ReloadSourcesAsync();
            await LoadPageAsync();

            // A complete scan is followed by read-only System.Rating import for THIS source.
            // It runs in the background, in batches, and can be canceled at any time.
            if (result.Completed)
            {
                var imported = await Task.Run(async () =>
                {
                    int count = 0;
                    while (true)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        int step = await _ratingImporter.ImportNextBatchAsync(64, cts.Token, source.Id);
                        if (step == 0) break;
                        count += step;
                        int snapshot = count;
                        _uiDispatcher.TryEnqueue(() =>
                            OperationStatus.Text = $"Wczytywanie klasyfikacji: {snapshot:N0} · {source.Root}");
                    }
                    return count;
                }, cts.Token);

                OperationStatus.Text = $"Zindeksowano {result.Discovered:N0} plików i odczytano klasyfikację {imported:N0} plików.";
                await LoadPageAsync();
            }
        }
        catch (OperationCanceledException)
        {
            OperationStatus.Text = "Anulowano indeksowanie lub odczyt klasyfikacji. Zapisane wpisy pozostają w SQLite.";
            await LoadPageAsync();
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "Błąd indeksowania (bez czyszczenia katalogu): " + ex.Message;
        }
        finally
        {
            SetOperation(cts, false);
        }
    }

    private async void ImportRatings_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady || _activeOperation is not null) return;
        using var cts = new CancellationTokenSource();
        SetOperation(cts, true);
        OperationStatus.Text = "Odczyt klasyfikacji z plików — bez zapisywania metadanych.";
        try
        {
            var total = await Task.Run(async () =>
            {
                var counter = 0;
                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var step = await _ratingImporter.ImportNextBatchAsync(64, cts.Token);
                    if (step == 0) break;
                    counter += step;
                    var count = counter;
                    _uiDispatcher.TryEnqueue(() =>
                        OperationStatus.Text = $"Odczytano klasyfikacje {count:N0} plików...");
                }
                return counter;
            }, cts.Token);
            OperationStatus.Text = $"Odczyt klasyfikacji zakończony ({total:N0} plików).";
            await LoadPageAsync();
        }
        catch (OperationCanceledException)
        {
            OperationStatus.Text = "Odczyt ocen anulowany. Zapisany postęp pozostał w SQLite.";
            await LoadPageAsync();
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "Nie udało się odczytać ocen: " + ex.Message;
        }
        finally
        {
            SetOperation(cts, false);
        }
    }

    private void SetOperation(CancellationTokenSource source, bool active)
    {
        _activeOperation = active ? source : null;
        ScanButton.IsEnabled = !active;
        RatingButton.IsEnabled = !active;
        AddSourceButton.IsEnabled = !active;
        BrowseSourceButton.IsEnabled = !active;
        CancelButton.IsEnabled = active;
        ScanProgress.IsIndeterminate = active;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _activeOperation?.Cancel();
        OperationStatus.Text = "Trwa anulowanie bieżącej operacji...";
    }
}
