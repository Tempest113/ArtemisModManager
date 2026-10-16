using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Artemis.App.ViewModels;
using Artemis.Core.Game;
using Artemis.Core.Playsets;

namespace Artemis.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;
        StateChanged += (_, _) => OnStateChanged();
        PreviewKeyDown += Window_PreviewKeyDown;
        InstalledList.PreviewMouseWheel += ScrollThreeRows;
        OrderList.PreviewMouseWheel += ScrollThreeRows;
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;
            if (e.ChangedButton == MouseButton.XButton1) _vm.GoBack();
            else _vm.GoForward();
            e.Handled = true;
        };
        Activated += (_, _) => _vm.CheckDrift();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LibraryCollapsed)) ApplyLibraryCollapsed(_vm.LibraryCollapsed);
        };
        _vm.LogChanged += ShowLogChange;
        RestoreLayout();
        Closing += (_, _) => SaveLayout();
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnMessage);
        _vm.ClearInstalledSelectionRequested += () => InstalledList.UnselectAll();
        Loaded += async (_, _) => await _vm.InitializeAsync();
    }

    // ---- live log ----

    private int _logChars;

    /// <summary>
    /// Lines added to the log are appended, so a selection and the scroll position survive. The view follows the end of the log (the newest part) unless the
    /// reader has scrolled up to read; a new text always starts at the end. Past about a million characters the oldest half is dropped.
    /// </summary>
    private void ShowLogChange(LogChange change)
    {
        var follow = change.Replace || LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 4;
        if (change.Replace) LogBox.Text = change.Text;
        else LogBox.AppendText(change.Text);
        _logChars = change.Replace ? change.Text.Length : _logChars + change.Text.Length;
        if (_logChars > 1_000_000)
        {
            var text = LogBox.Text;
            LogBox.Text = text = text[(text.IndexOf('\n', text.Length / 2) + 1)..];
            _logChars = text.Length;
        }
        if (follow) Dispatcher.BeginInvoke(LogBox.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---- remembered layout: window position and size, the split between the two lists, whether Installed is collapsed ----

    private GridLength _libraryShare = new(1, GridUnitType.Star), _orderShare = new(1, GridUnitType.Star);

    private void RestoreLayout()
    {
        var settings = _vm.Settings;
        if (settings.Window is { } w)
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            Width = Math.Clamp(w.Width, MinWidth, Math.Max(MinWidth, screen.Width));
            Height = Math.Clamp(w.Height, MinHeight, Math.Max(MinHeight, screen.Height));
            // Keep some of the title bar on a screen even if the monitor it was on is gone.
            Left = Math.Clamp(w.Left, screen.Left - Width + 120, screen.Right - 120);
            Top = Math.Clamp(w.Top, screen.Top, screen.Bottom - 60);
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (w.Maximized) WindowState = WindowState.Maximized;
        }
        if (settings.LibrarySplit is > 0.1 and < 0.9 and var split)
        {
            _libraryShare = new GridLength(split, GridUnitType.Star);
            _orderShare = new GridLength(1 - split, GridUnitType.Star);
            ApplyLibraryCollapsed(false);
        }
        if (settings.LibraryCollapsed) _vm.LibraryCollapsed = true;
    }

    private void SaveLayout()
    {
        var settings = _vm.Settings;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        settings.Window = new WindowPlacement { Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height, Maximized = WindowState == WindowState.Maximized };
        settings.LibraryCollapsed = _vm.LibraryCollapsed;
        if (!_vm.LibraryCollapsed && LibraryColumn.ActualWidth > 0) settings.LibrarySplit = LibraryColumn.ActualWidth / (LibraryColumn.ActualWidth + OrderColumn.ActualWidth);
        else if (_libraryShare.Value + _orderShare.Value > 0) settings.LibrarySplit = _libraryShare.Value / (_libraryShare.Value + _orderShare.Value);
        _vm.SaveSettings();
    }

    /// <summary>Collapsing keeps the dragged split, to bring back on expanding.</summary>
    private void ApplyLibraryCollapsed(bool collapsed)
    {
        if (collapsed)
        {
            _libraryShare = LibraryColumn.Width;
            _orderShare = OrderColumn.Width;
            LibraryColumn.MinWidth = 0;
            LibraryColumn.Width = new GridLength(48);
            OrderColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            LibraryColumn.MinWidth = 300;
            LibraryColumn.Width = _libraryShare;
            OrderColumn.Width = _orderShare;
        }
        PaneSplitter.IsEnabled = !collapsed;
    }

    // ---- window chrome ----

    private void OnStateChanged()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaxGlyph.Text = (string)FindResource(maximized ? "IcoRestore" : "IcoMax");
        System.Windows.Automation.AutomationProperties.SetName(MaxButton, maximized ? "Restore" : "Maximize");
    }

    // A borderless window maximises over the taskbar unless it is told the usable area of the monitor it is on.
    // Answering this message ourselves also means supplying the minimum size WPF would have given.
    [StructLayout(LayoutKind.Sequential)] private struct Pt { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rc { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Pt Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rc Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024, WmMouseHWheel = 0x020E, MonitorDefaultToNearest = 2;
        if (msg == WmMouseHWheel)
        {
            handled = PanSideways((short)((long)wParam >> 16));
            return IntPtr.Zero;
        }
        if (msg != WmGetMinMaxInfo) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        mmi.MaxPosition = new Pt { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
        mmi.MaxSize = new Pt { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
        var dpi = VisualTreeHelper.GetDpi(this);
        mmi.MinTrack = new Pt { X = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX), Y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY) };
        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>
    /// WPF ignores the sideways tilt of a mouse wheel, so the scroll viewer under the pointer is moved here. A tilt to the right
    /// (positive) moves the view right. Returns false when nothing under the pointer can scroll sideways.
    /// </summary>
    private static bool PanSideways(int delta)
    {
        for (var element = Mouse.DirectlyOver as DependencyObject; element is not null; element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is not ScrollViewer { ScrollableWidth: > 0 } viewer) continue;
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + delta / 120.0 * 48);
            return true;
        }
        return false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    // ---- menus (built in code so they bind straight to the view model) ----

    private string Glyph(string key) => (string)FindResource(key);

    private MenuItem Item(string header, string? glyphKey, Action click, bool enabled = true)
    {
        var item = new MenuItem { Header = header, Tag = glyphKey is null ? null : Glyph(glyphKey), IsEnabled = enabled };
        item.Click += (_, _) => click();
        return item;
    }

    private static void Show(ContextMenu menu, FrameworkElement target)
    {
        // Custom placement, not Bottom or Relative: WPF flips both to right-aligned when Windows' "menus on the
        // right-hand side" setting is on, which put these menus off to the left of their buttons.
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (_, size, _) =>
            [new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(0, size.Height + 2), System.Windows.Controls.Primitives.PopupPrimaryAxis.None)];
        menu.IsOpen = true;
    }

    private void PlaysetMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { MinWidth = PlaysetButton.ActualWidth };
        foreach (var playset in _vm.Playsets)
        {
            var item = new MenuItem { Header = playset.Name, IsChecked = playset == _vm.SelectedPlayset };
            item.Click += (_, _) => _vm.SelectedPlayset = playset;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("New playset…", "IcoAdd", () => _vm.NewPlaysetCommand.Execute(null)));
        menu.Items.Add(Item("Duplicate", "IcoLink", () => _vm.DuplicatePlaysetCommand.Execute(null)));
        menu.Items.Add(Item("Rename…", "IcoEdit", () => _vm.RenamePlaysetCommand.Execute(null)));
        menu.Items.Add(Item("Delete…", "IcoDelete", () => _vm.DeletePlaysetCommand.Execute(null)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Import from other tools…", "IcoFolder", () => _vm.ImportPlaysetsCommand.Execute(null)));
        menu.Items.Add(Item("Import from a file…", "IcoFolder", () => _vm.ImportPlaysetFileCommand.Execute(null)));
        menu.Items.Add(Item("Export to a file…", "IcoSave", () => _vm.ExportPlaysetCommand.Execute(null)));
        Show(menu, PlaysetButton);
    }

    private void AddMod_Click(object sender, RoutedEventArgs e) =>
        Show(new ContextMenu
        {
            Items =
            {
                Item("Create a mod…", "IcoAdd", () => _vm.CreateModCommand.Execute(null)),
                Item("Install from an archive…", "IcoFolder", () => _vm.InstallFromArchiveCommand.Execute(null)),
                Item("Clear leftover .mod files…", "IcoFolder", () => _vm.ReviewLeftoverModsCommand.Execute(null)),
            },
        }, AddModButton);

    private void LaunchMenu_Click(object sender, RoutedEventArgs e) =>
        Show(new ContextMenu
        {
            Items =
            {
                Item("Launch without plugins (this time)", "IcoPlay", () => _vm.LaunchWithoutPluginsCommand.Execute(null), enabled: !_vm.GameRunning),
                Item("Save the load order without launching", "IcoSave", () => _vm.ApplyOnlyCommand.Execute(null)),
                Item("Restore the original dlc_load.json", "IcoUndo", () => _vm.RestoreOriginalCommand.Execute(null)),
            },
        }, LaunchMenuButton);

    private static readonly string[] SeparatorPresets = ["Gameplay", "UI", "Fixes", "Shipsets & portraits", "Compatibility patches"];

    /// <summary>The choices for a new separator at a line of the load order: a few common names, or one of the user's own.</summary>
    private IEnumerable<Control> SeparatorItems(int index)
    {
        foreach (var name in SeparatorPresets) yield return Item(name, null, () => _vm.AddSeparator(name, index));
        yield return new Separator();
        yield return Item("Custom…", "IcoEdit", () => _vm.AddCustomSeparator(index));
    }

    private void AddSeparator_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var item in SeparatorItems(_vm.LoadOrder.Count)) menu.Items.Add(item);
        Show(menu, AddSeparatorButton);
    }

    // ---- Installed list ----

    private void InstalledFilter_Click(object sender, RoutedEventArgs e)
    {
        MenuItem Choice(string header, bool selected, Action pick)
        {
            var item = new MenuItem { Header = header, IsChecked = selected };
            item.Click += (_, _) => pick();
            return item;
        }
        MenuItem Label(string text) => new() { Header = text, IsEnabled = false, FontSize = 11.5 };

        var menu = new ContextMenu();
        menu.Items.Add(Label("Show"));
        foreach (var (filter, text) in new[]
        {
            (InstalledFilter.All, "All mods"),
            (InstalledFilter.NotInPlayset, "Not in this playset"),
            (InstalledFilter.InPlayset, "In this playset"),
            (InstalledFilter.Outdated, "Made for an older game version"),
            (InstalledFilter.Missing, "With missing files"),
            (InstalledFilter.NewOrUpdated, "New or updated since my last launch"),
            (InstalledFilter.WithConflicts, "With conflicts with other mods"),
            (InstalledFilter.WithPlugin, "With a plugin"),
        })
            menu.Items.Add(Choice(text, _vm.Filter == filter, () => _vm.Filter = filter));
        menu.Items.Add(new Separator());
        menu.Items.Add(Label("Sort by"));
        menu.Items.Add(Choice("Name", _vm.Sort == InstalledSort.Name, () => _vm.Sort = InstalledSort.Name));
        menu.Items.Add(Choice("Recently added", _vm.Sort == InstalledSort.Newest, () => _vm.Sort = InstalledSort.Newest));
        Show(menu, FilterButton);
    }

    private void InstalledList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _vm.SetInstalledSelection(InstalledList.SelectedItems.OfType<InstalledRowViewModel>());
        if (InstalledList.SelectedItems.Count == 1 && InstalledList.SelectedItem is ModRowBase row) _vm.FollowSelection(row);
    }

    private void InstalledRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        var item = (ListBoxItem)sender;
        if (!item.IsSelected)
        {
            InstalledList.SelectedItems.Clear();
            item.IsSelected = true;
        }
        var rows = _vm.SelectedInstalled.Count > 0 ? _vm.SelectedInstalled : [(InstalledRowViewModel)item.DataContext];
        var single = rows.Count == 1 ? rows[0] : null;

        var menu = new ContextMenu();
        menu.Items.Add(Item("Show details", "IcoInfo", () => { _vm.FollowSelection(rows[0]); _vm.DetailsOpen = true; }));
        menu.Items.Add(new Separator());
        var allIn = rows.All(r => r.InPlayset);
        menu.Items.Add(allIn
            ? Item("Remove from playset", "IcoClose", () => { foreach (var r in rows.ToList()) r.InPlayset = false; })
            : Item("Add to playset", "IcoAdd", () => { foreach (var r in rows.ToList()) r.InPlayset = true; }));
        if (single?.Mod is { } mod)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Open folder", "IcoFolder", () => _vm.OpenFolderOf(mod), enabled: mod.ContentDir is not null));
            if (mod.RemoteFileId is { Length: > 0 }) menu.Items.Add(Item("Open on the Workshop", "IcoLink", () => _vm.OpenOnWorkshop(mod)));
        }
        item.ContextMenu = menu;
    }

    // ---- Load order list ----

    private void OrderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OrderList.SelectedItems.Count != 1) return;
        OrderList.ScrollIntoView(OrderList.SelectedItem);
        if (OrderList.SelectedItem is LoadOrderRowViewModel { IsSeparator: false } row) _vm.FollowSelection(row);
    }

    /// <summary>The selected rows in list order.</summary>
    private List<LoadOrderRowViewModel> SelectedOrderRows() =>
        OrderList.SelectedItems.OfType<LoadOrderRowViewModel>().OrderBy(_vm.LoadOrder.IndexOf).ToList();

    private void OrderRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        var item = (ListBoxItem)sender;
        if (!item.IsSelected)
        {
            OrderList.SelectedItems.Clear();
            item.IsSelected = true;
        }
        var rows = SelectedOrderRows();
        var menu = new ContextMenu();
        if (rows is [{ IsSeparator: true } separator])
        {
            menu.Items.Add(Item("Rename…", "IcoEdit", () => _vm.RenameSeparator(separator)));
            menu.Items.Add(Item("Remove separator", "IcoClose", () => _vm.RemoveRows(rows)));
            item.ContextMenu = menu;
            return;
        }

        if (rows is [{ } first]) menu.Items.Add(Item("Show details", "IcoInfo", () => { _vm.FollowSelection(first); _vm.DetailsOpen = true; }));
        menu.Items.Add(Item("Move to top", "IcoUp", () => _vm.MoveRows(rows, 0)));
        menu.Items.Add(Item("Move to bottom", "IcoDown", () => _vm.MoveRows(rows, int.MaxValue)));
        menu.Items.Add(new Separator());
        var above = new MenuItem { Header = "Add separator above", Tag = Glyph("IcoAdd") };
        foreach (var c in SeparatorItems(_vm.LoadOrder.IndexOf(rows[0]))) above.Items.Add(c);
        menu.Items.Add(above);
        if (rows is [{ Mod: { } mod }])
        {
            menu.Items.Add(Item("Open folder", "IcoFolder", () => _vm.OpenFolderOf(mod), enabled: mod.ContentDir is not null));
            if (mod.RemoteFileId is { Length: > 0 }) menu.Items.Add(Item("Open on the Workshop", "IcoLink", () => _vm.OpenOnWorkshop(mod)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove from playset", "IcoClose", () => _vm.RemoveRows(rows)));
        item.ContextMenu = menu;
    }

    private void PositionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var box = (TextBox)sender;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
        OrderList.Focus();
    }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _vm.FindText = "";
            OrderList.Focus();
        }
        else if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _vm.FindPreviousCommand.Execute(null);
            else _vm.FindNextCommand.Execute(null);
        }
        else return;
        e.Handled = true;
    }

    // ---- keyboard ----

    // Ctrl+F finds in the load order, Ctrl+Shift+F searches the installed list, F3 / Shift+F3 step through matches,
    // Ctrl+Z / Ctrl+Y (or Ctrl+Shift+Z) undo and redo load order changes unless a text box has focus.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.System && e.SystemKey == Key.Enter && Keyboard.Modifiers == ModifierKeys.Alt) _vm.ToggleDetailsCommand.Execute(null);
        else if (e.Key == Key.Escape && _vm.DetailsOpen && Keyboard.FocusedElement is not TextBoxBase) _vm.CloseDetailsCommand.Execute(null);
        else if (ctrl && e.Key == Key.F)
        {
            var box = shift ? InstalledSearchBox : FindBox;
            box.Focus();
            box.SelectAll();
        }
        else if (e.Key == Key.F3)
            (shift ? _vm.FindPreviousCommand : _vm.FindNextCommand).Execute(null);
        else if (ctrl && e.Key is Key.Z or Key.Y && Keyboard.FocusedElement is not TextBoxBase)
            (e.Key == Key.Y || shift ? _vm.RedoCommand : _vm.UndoCommand).Execute(null);
        else return;
        e.Handled = true;
    }

    private void InstalledList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter adds the selected mods to the playset, or removes them if they are all in it.
        if (e.Key != Key.Enter || e.OriginalSource is TextBoxBase || _vm.SelectedInstalled.Count == 0) return;
        var rows = _vm.SelectedInstalled.ToList();
        var on = !rows.All(r => r.InPlayset);
        foreach (var row in rows) row.InPlayset = on;
        e.Handled = true;
    }

    private void OrderList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBoxBase) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var rows = SelectedOrderRows();
        if (rows.Count == 0) return;
        switch (key)
        {
            case Key.Delete:
                _vm.RemoveRows(rows);
                OrderList.Focus();
                break;
            case Key.Space:
                _vm.ToggleEnabled(rows);
                break;
            case Key.F2 when rows is [{ IsSeparator: true } separator]:
                _vm.RenameSeparator(separator);
                break;
            case Key.Up or Key.Down when Keyboard.Modifiers == ModifierKeys.Alt:
                _vm.MoveRowsBy(rows, key == Key.Up ? -1 : 1);
                OrderList.ScrollIntoView(rows[0]);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ---- drag to reorder: grab the row anywhere except its own controls; a multi-selection moves as one ----

    private Point? _dragStart;
    private bool _collapseOnRelease;

    private static bool IsOnControl(DependencyObject? o)
    {
        for (; o is not null && o is not ListBoxItem; o = o is Visual ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o))
            if (o is TextBoxBase or ButtonBase) return true;
        return false;
    }

    private void OrderRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var item = (ListBoxItem)sender;
        _collapseOnRelease = false;
        if (IsOnControl(e.OriginalSource as DependencyObject))
        {
            _dragStart = null;
            return;
        }
        _dragStart = e.GetPosition(null);
        // Pressing on a selected row would normally shrink the selection to it, which would make a multi-drag impossible.
        // Keep the selection until we know it was a plain click.
        if (item.IsSelected && OrderList.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _collapseOnRelease = true;
            e.Handled = true;
        }
    }

    private void OrderRow_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_collapseOnRelease) return;
        _collapseOnRelease = false;
        OrderList.SelectedItems.Clear();
        ((ListBoxItem)sender).IsSelected = true;
    }

    private void OrderRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var moved = e.GetPosition(null) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        _collapseOnRelease = false;
        var item = (ListBoxItem)sender;
        if (!item.IsSelected)
        {
            OrderList.SelectedItems.Clear();
            item.IsSelected = true;
        }
        DragDrop.DoDragDrop(item, new DataObject(typeof(LoadOrderRowViewModel[]), SelectedOrderRows().ToArray()), DragDropEffects.Move);
        Mark(null, DropMark.None);
    }

    private static bool IsRow(DragEventArgs e) => e.Data.GetDataPresent(typeof(LoadOrderRowViewModel[]));

    private static LoadOrderRowViewModel[] DraggedRows(DragEventArgs e) => (LoadOrderRowViewModel[])e.Data.GetData(typeof(LoadOrderRowViewModel[]))!;

    private LoadOrderRowViewModel? _marked;

    /// <summary>Shows the drop line on one row (or none).</summary>
    private void Mark(LoadOrderRowViewModel? row, DropMark mark)
    {
        if (_marked is not null && _marked != row) _marked.Drop = DropMark.None;
        _marked = row;
        if (row is not null) row.Drop = mark;
    }

    private void OrderRow_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (!IsRow(e) || ((FrameworkElement)sender).DataContext is not LoadOrderRowViewModel target) return;
        var rows = DraggedRows(e);
        e.Effects = DragDropEffects.Move;
        // Same rule as the drop itself: dragging up lands above the target, dragging down below it.
        if (rows.Contains(target)) Mark(null, DropMark.None);
        else Mark(target, rows.Min(_vm.LoadOrder.IndexOf) > _vm.LoadOrder.IndexOf(target) ? DropMark.Above : DropMark.Below);
    }

    private void OrderRow_Drop(object sender, DragEventArgs e)
    {
        Mark(null, DropMark.None);
        if (!IsRow(e) || ((FrameworkElement)sender).DataContext is not LoadOrderRowViewModel target) return;
        _vm.MoveRowsTo(DraggedRows(e), target);
        e.Handled = true;
    }

    // Below the last row: drop at the very end.
    private void OrderList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = IsRow(e) ? DragDropEffects.Move : DragDropEffects.None;
        if (IsRow(e) && _vm.LoadOrder.Count > 0) Mark(_vm.LoadOrder[^1], DropMark.Below);
        e.Handled = true;
    }

    private void OrderList_Drop(object sender, DragEventArgs e)
    {
        Mark(null, DropMark.None);
        if (IsRow(e)) _vm.MoveRows(DraggedRows(e), int.MaxValue);
        e.Handled = true;
    }

    // Dragging near the top or bottom edge of the list scrolls it (DragOver repeats while the mouse is held still).
    private ScrollViewer? _orderScroll;

    private void OrderList_PreviewDragOver(object sender, DragEventArgs e)
    {
        _orderScroll ??= FindDescendant<ScrollViewer>(OrderList);
        const double edge = 40;
        var y = e.GetPosition(OrderList).Y;
        if (y < edge) _orderScroll?.ScrollToVerticalOffset(_orderScroll.VerticalOffset - RowHeight);
        else if (y > OrderList.ActualHeight - edge) _orderScroll?.ScrollToVerticalOffset(_orderScroll.VerticalOffset + RowHeight);
    }

    // The lists scroll by pixel, so rows of different heights (separators, group headers) still end flush at the bottom.
    // A wheel notch then moves 48 px by default; keep the three rows it moved when the lists scrolled by row.
    private const double RowHeight = 40;

    private static void ScrollThreeRows(object sender, MouseWheelEventArgs e)
    {
        if (FindDescendant<ScrollViewer>((DependencyObject)sender) is not { } viewer) return;
        viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}

/// <summary>Mods and separators share the load order list but look different.</summary>
public sealed class OrderTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Mod { get; set; }
    public DataTemplate? Separator { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is LoadOrderRowViewModel { IsSeparator: true } ? Separator : Mod;
}
