using System.Windows;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rafferty.UI;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(string heading, string description, string text)
    {
        InitializeComponent();
        Heading.Text = heading;
        Description.Text = description;
        Output.Text = text;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    internal void RenderForTest(string path)
    {
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
