using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace LocalDeck.App.Windows;

public partial class PinWindow : Window
{
    private readonly BitmapSource _image;
    private Point _dragStart;

    public PinWindow(BitmapSource image)
    {
        InitializeComponent();
        _image = image;
        PinnedImage.Source = image;

        var maxWidth = SystemParameters.WorkArea.Width * 0.7;
        var maxHeight = SystemParameters.WorkArea.Height * 0.7;
        var scale = Math.Min(1, Math.Min(maxWidth / image.PixelWidth, maxHeight / image.PixelHeight));
        Width = Math.Max(160, image.PixelWidth * scale);
        Height = Math.Max(100, image.PixelHeight * scale);
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
        Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;

        PreviewKeyDown += OnPreviewKeyDown;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseRightButtonUp += (_, _) => Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Clipboard.SetImage(_image);
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Close();
            return;
        }

        _dragStart = e.GetPosition(this);
        DragMove();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && _dragStart == default)
        {
            DragMove();
        }
    }
}
