using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using LocalDeck.App.Windows;

namespace LocalDeck.App.Services;

public sealed class ScreenshotService
{
    public async Task<ScreenshotResult?> CaptureRegionAsync(
        Window owner, bool pinAfterCapture = false)
    {
        owner.Hide();
        await Task.Delay(180);
        Bitmap? screenshot = null;
        try
        {
            screenshot = CaptureVirtualScreen();
            var source = ToBitmapSource(screenshot);
            var selector = new RegionSelectorWindow(source) { Owner = owner };
            owner.Show();
            var accepted = selector.ShowDialog() == true;
            if (!accepted)
            {
                return null;
            }

            var cropped = new CroppedBitmap(source, selector.SelectedPixels);
            cropped.Freeze();
            Clipboard.SetImage(cropped);

            var filePath = Path.Combine(
                AppPaths.ScreenshotDirectory,
                $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            SavePng(cropped, filePath);

            if (pinAfterCapture)
            {
                var pin = new PinWindow(cropped);
                pin.Show();
            }
            return new ScreenshotResult(
                filePath,
                selector.SelectedPixels.Width,
                selector.SelectedPixels.Height);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Screenshot failed: {ex}");
            return new ScreenshotResult(null, 0, 0, ex.Message);
        }
        finally
        {
            screenshot?.Dispose();
            if (!owner.IsVisible)
            {
                owner.Show();
            }

            owner.Activate();
        }
    }

    public bool PinClipboardImage()
    {
        try
        {
            if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image)
            {
                return false;
            }

            image.Freeze();
            new PinWindow(image).Show();
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Pin clipboard image failed: {ex.Message}");
            return false;
        }
    }

    public bool CaptureFullScreen()
    {
        try
        {
            using var screenshot = CaptureVirtualScreen();
            var source = ToBitmapSource(screenshot);
            source.Freeze();
            Clipboard.SetImage(source);
            var filePath = Path.Combine(
                AppPaths.ScreenshotDirectory,
                $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            SavePng(source, filePath);
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Full-screen capture failed: {ex}");
            return false;
        }
    }

    private static Bitmap CaptureVirtualScreen()
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    private static void SavePng(BitmapSource source, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(stream);
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}

public sealed record ScreenshotResult(
    string? Path, int Width, int Height, string? Error = null);
