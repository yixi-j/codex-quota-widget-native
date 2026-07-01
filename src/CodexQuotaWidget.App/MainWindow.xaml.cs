using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaWidget.Core;
using MediaColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;

namespace CodexQuotaWidget.App;

public partial class MainWindow : Window
{
    private const double MinWindowWidth = 420;
    private const double MinWindowHeight = 44;
    private const double MaxWindowWidth = 1000;
    private const double MaxWindowHeight = 160;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsCaption = 0x00C00000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private readonly Action<WindowConfig> _saveWindowConfig;
    private readonly AppLogger _logger;
    private readonly DispatcherTimer _topmostTimer;
    private WindowConfig _config;
    private bool _sourceReady;
    private double? _pendingResizeWidth;
    private double? _pendingResizeHeight;

    public MainWindow(WidgetConfig config, Action<WindowConfig> saveWindowConfig, AppLogger logger)
    {
        _config = config.Window;
        _saveWindowConfig = saveWindowConfig;
        _logger = logger;
        InitializeComponent();
        _topmostTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _topmostTimer.Tick += (_, _) => EnforceTopmost();
        _topmostTimer.Start();
        SourceInitialized += (_, _) =>
        {
            _sourceReady = true;
            RemoveCaptionStyle();
            ApplyWindowConfig(_config);
            SetClickThrough(_config.ClickThrough);
        };
        Loaded += (_, _) => ApplyInitialPosition();
        Closed += (_, _) => _topmostTimer.Stop();
    }

    public bool IsEditMode { get; private set; }
    public bool IsClickThrough { get; private set; }

    public void ApplyWindowConfig(WindowConfig config)
    {
        _config = config;
        Width = Clamp(config.Width, MinWindowWidth, MaxWindowWidth);
        Height = Clamp(config.Height, MinWindowHeight, MaxWindowHeight);
        Opacity = config.Opacity;
        FontSize = config.FontSize;
        Topmost = config.AlwaysOnTop;
        if (_sourceReady)
        {
            SetClickThrough(config.ClickThrough && !IsEditMode);
            EnforceTopmost();
        }
    }

    public void ApplyDisplay(DisplayModel model)
    {
        TitleText.Text = model.Title;
        TitleText.Visibility = string.IsNullOrWhiteSpace(model.Title) ? Visibility.Collapsed : Visibility.Visible;
        QuotaLeft.Text = model.QuotaLeft;
        QuotaSeparator.Text = model.QuotaSeparator;
        QuotaRight.Text = model.QuotaRight;
        ResetLeft.Text = model.ResetLeft;
        ResetSeparator.Text = model.ResetSeparator;
        ResetRight.Text = model.ResetRight;
        RefreshLeft.Text = model.RefreshLeft;
        RefreshSeparator.Text = model.RefreshSeparator;
        RefreshRight.Text = model.RefreshRight;

        var quotaBrush = model.IsWarning
            ? new SolidColorBrush(MediaColor.FromRgb(255, 204, 102))
            : new SolidColorBrush(MediaColor.FromRgb(245, 247, 250));
        QuotaLeft.Foreground = quotaBrush;
        QuotaRight.Foreground = quotaBrush;
    }

    public void SetEditMode(bool enabled)
    {
        IsEditMode = enabled;
        EditBadge.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ResizeThumb.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        Card.BorderBrush = enabled
            ? new SolidColorBrush(MediaColor.FromArgb(180, 74, 144, 226))
            : new SolidColorBrush(MediaColor.FromArgb(34, 255, 255, 255));
        Cursor = enabled ? WpfCursors.SizeAll : WpfCursors.Arrow;
        SetClickThrough(!enabled && _config.ClickThrough);
        _logger.Info(enabled ? "进入编辑位置和大小模式" : "锁定位置和大小");
        if (!enabled)
        {
            SaveWindowGeometry();
        }
    }

    public void ToggleEditMode()
    {
        SetEditMode(!IsEditMode);
    }

    public string GetBodyText()
    {
        return string.Join("  ", new[]
        {
            TitleText.Text,
            $"{QuotaLeft.Text} {QuotaSeparator.Text} {QuotaRight.Text}",
            $"{ResetLeft.Text} {ResetSeparator.Text} {ResetRight.Text}",
            $"{RefreshLeft.Text} {RefreshSeparator.Text} {RefreshRight.Text}",
            IsEditMode ? "编辑" : ""
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    public WindowVerificationSnapshot VerifyWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GwlStyle);
        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        return new WindowVerificationSnapshot
        {
            BodyText = GetBodyText(),
            HasCaption = (style & WsCaption) == WsCaption,
            HasToolWindow = (exStyle & WsExToolWindow) != 0,
            HasLayered = (exStyle & WsExLayered) != 0,
            HasTransparent = (exStyle & WsExTransparent) != 0,
            ShowInTaskbar = ShowInTaskbar,
            Topmost = Topmost,
            IsEditMode = IsEditMode,
            IsResizeHandleVisible = ResizeThumb.Visibility == Visibility.Visible,
            IsOnTaskbarLeft = IsOnTaskbarLeft(Left, Top, Width, Height),
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height
        };
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsEditMode || IsFromResizeHandle(e.OriginalSource))
        {
            return;
        }

        try
        {
            DragMove();
            SaveWindowGeometry();
        }
        catch (InvalidOperationException)
        {
            // DragMove 要求鼠标左键处于按下状态，自动化验证时可能不满足。
        }
    }

    private void ApplyInitialPosition()
    {
        if (_config.X is not null && _config.Y is not null)
        {
            Left = _config.X.Value;
            Top = _config.Y.Value;
            return;
        }

        var area = SystemParameters.WorkArea;
        Left = area.Left + 80;
        Top = Math.Max(area.Top + 40, area.Bottom - Height - 80);
    }

    public void ResizeBy(double deltaWidth, double deltaHeight)
    {
        ResizeTo(CurrentWidth() + deltaWidth, CurrentHeight() + deltaHeight);
    }

    public void ResizeTo(double width, double height)
    {
        var nextWidth = Clamp(width, MinWindowWidth, MaxWindowWidth);
        var nextHeight = Clamp(height, MinWindowHeight, MaxWindowHeight);
        SetCurrentValue(WidthProperty, nextWidth);
        SetCurrentValue(HeightProperty, nextHeight);
        _config = _config with { Width = nextWidth, Height = nextHeight };
        _pendingResizeWidth = nextWidth;
        _pendingResizeHeight = nextHeight;
        UpdateLayout();
    }

    public void SnapToTaskbarLeft()
    {
        var screen = PrimaryScreenRect();
        var taskbar = TaskbarRect(screen, SystemParameters.WorkArea);
        var width = Clamp(CurrentWidth(), MinWindowWidth, MaxWindowWidth);
        var height = Clamp(CurrentHeight(), MinWindowHeight, MaxWindowHeight);
        var margin = 4.0;

        if (taskbar.IsEmpty)
        {
            Left = screen.Left + margin;
            Top = Clamp(screen.Bottom - height - margin, screen.Top, screen.Bottom - height);
        }
        else
        {
            Left = Clamp(taskbar.Left + margin, screen.Left, screen.Right - width);
            Top = Clamp(taskbar.Top + (taskbar.Height - height) / 2, screen.Top, screen.Bottom - height);
        }

        SaveWindowGeometry();
        EnforceTopmost();
    }

    public void EnforceTopmost()
    {
        if (!_sourceReady || !_config.AlwaysOnTop || !IsVisible)
        {
            return;
        }

        Topmost = true;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!IsEditMode)
        {
            return;
        }

        ResizeBy(e.HorizontalChange, e.VerticalChange);
    }

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        SaveWindowGeometry();
    }

    private void SaveWindowGeometry()
    {
        var savedWidth = Clamp(Math.Max(_pendingResizeWidth ?? CurrentWidth(), _config.Width), MinWindowWidth, MaxWindowWidth);
        var savedHeight = Clamp(Math.Max(_pendingResizeHeight ?? CurrentHeight(), _config.Height), MinWindowHeight, MaxWindowHeight);
        var next = _config with
        {
            X = Left,
            Y = Top,
            Width = savedWidth,
            Height = savedHeight
        };
        _config = next;
        _pendingResizeWidth = null;
        _pendingResizeHeight = null;
        _saveWindowConfig(next);
        _logger.Info("保存窗口位置和大小", new { x = Left, y = Top, width = savedWidth, height = savedHeight });
    }

    private bool IsFromResizeHandle(object source)
    {
        if (source is not DependencyObject current)
        {
            return false;
        }

        while (current is not null)
        {
            if (ReferenceEquals(current, ResizeThumb))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max < min)
        {
            return min;
        }

        return Math.Min(Math.Max(value, min), max);
    }

    private static Rect PrimaryScreenRect()
    {
        return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
    }

    private static Rect TaskbarRect(Rect screen, Rect workArea)
    {
        const double epsilon = 0.1;

        if (workArea.Bottom < screen.Bottom - epsilon)
        {
            return new Rect(screen.Left, workArea.Bottom, screen.Width, screen.Bottom - workArea.Bottom);
        }

        if (workArea.Top > screen.Top + epsilon)
        {
            return new Rect(screen.Left, screen.Top, screen.Width, workArea.Top - screen.Top);
        }

        if (workArea.Left > screen.Left + epsilon)
        {
            return new Rect(screen.Left, screen.Top, workArea.Left - screen.Left, screen.Height);
        }

        if (workArea.Right < screen.Right - epsilon)
        {
            return new Rect(workArea.Right, screen.Top, screen.Right - workArea.Right, screen.Height);
        }

        return Rect.Empty;
    }

    private static bool IsOnTaskbarLeft(double left, double top, double width, double height)
    {
        var screen = PrimaryScreenRect();
        var taskbar = TaskbarRect(screen, SystemParameters.WorkArea);
        var window = new Rect(left, top, width, height);

        if (taskbar.IsEmpty)
        {
            return left <= screen.Left + 8 &&
                   top >= screen.Top &&
                   top + height <= screen.Bottom + 0.1;
        }

        return left <= taskbar.Left + 8 && window.IntersectsWith(taskbar);
    }

    private double CurrentWidth()
    {
        return ActualWidth > 0 ? ActualWidth : Width;
    }

    private double CurrentHeight()
    {
        return ActualHeight > 0 ? ActualHeight : Height;
    }

    private void RemoveCaptionStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GwlStyle);
        SetWindowLong(hwnd, GwlStyle, style & ~WsCaption);
    }

    private void SetClickThrough(bool enabled)
    {
        IsClickThrough = enabled;
        if (!_sourceReady)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        exStyle |= WsExLayered | WsExToolWindow;
        if (enabled)
        {
            exStyle |= WsExTransparent | WsExNoActivate;
        }
        else
        {
            exStyle &= ~WsExTransparent;
            exStyle &= ~WsExNoActivate;
        }
        SetWindowLong(hwnd, GwlExStyle, exStyle);
        EnforceTopmost();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}

public sealed record WindowVerificationSnapshot
{
    public string BodyText { get; init; } = "";
    public bool HasCaption { get; init; }
    public bool HasToolWindow { get; init; }
    public bool HasLayered { get; init; }
    public bool HasTransparent { get; init; }
    public bool ShowInTaskbar { get; init; }
    public bool Topmost { get; init; }
    public bool IsEditMode { get; init; }
    public bool IsResizeHandleVisible { get; init; }
    public bool IsOnTaskbarLeft { get; init; }
    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}
