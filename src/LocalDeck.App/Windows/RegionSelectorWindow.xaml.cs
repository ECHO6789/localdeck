using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace LocalDeck.App.Windows;

public partial class RegionSelectorWindow : Window
{
    private readonly BitmapSource _screenshot;
    private Point _start;
    private bool _selecting;

    public Int32Rect SelectedPixels { get; private set; }

    public RegionSelectorWindow(BitmapSource screenshot)
    {
        InitializeComponent();
        _screenshot = screenshot;
        ScreenImage.Source = screenshot;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Root.MouseLeftButtonDown += OnMouseLeftButtonDown;
        Root.MouseMove += OnMouseMove;
        Root.MouseLeftButtonUp += OnMouseLeftButtonUp;
        Root.MouseRightButtonUp += (_, _) => DialogResult = false;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => Activate();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Root);
        _selecting = true;
        Hint.Visibility = Visibility.Collapsed;
        SelectionRectangle.Visibility = Visibility.Visible;
        System.Windows.Controls.Canvas.SetLeft(SelectionRectangle, _start.X);
        System.Windows.Controls.Canvas.SetTop(SelectionRectangle, _start.Y);
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_selecting)
        {
            return;
        }

        var current = e.GetPosition(Root);
        var left = Math.Min(_start.X, current.X);
        var top = Math.Min(_start.Y, current.Y);
        System.Windows.Controls.Canvas.SetLeft(SelectionRectangle, left);
        System.Windows.Controls.Canvas.SetTop(SelectionRectangle, top);
        SelectionRectangle.Width = Math.Abs(current.X - _start.X);
        SelectionRectangle.Height = Math.Abs(current.Y - _start.Y);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_selecting)
        {
            return;
        }

        _selecting = false;
        var current = e.GetPosition(Root);
        var left = Math.Min(_start.X, current.X);
        var top = Math.Min(_start.Y, current.Y);
        var width = Math.Abs(current.X - _start.X);
        var height = Math.Abs(current.Y - _start.Y);
        if (width < 5 || height < 5)
        {
            DialogResult = false;
            return;
        }

        var scaleX = _screenshot.PixelWidth / Math.Max(1, Root.ActualWidth);
        var scaleY = _screenshot.PixelHeight / Math.Max(1, Root.ActualHeight);
        SelectedPixels = new Int32Rect(
            (int)Math.Round(left * scaleX),
            (int)Math.Round(top * scaleY),
            Math.Max(1, (int)Math.Round(width * scaleX)),
            Math.Max(1, (int)Math.Round(height * scaleY)));
        SelectedPixels = ClampToImage(SelectedPixels, _screenshot.PixelWidth, _screenshot.PixelHeight);
        DialogResult = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }

    private static Int32Rect ClampToImage(Int32Rect rect, int width, int height)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        var right = Math.Clamp(rect.X + rect.Width, x + 1, width);
        var bottom = Math.Clamp(rect.Y + rect.Height, y + 1, height);
        return new Int32Rect(x, y, right - x, bottom - y);
    }
}
