using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DepotDumper.GUI;

public partial class QrCodeWindow : Window
{
    public QrCodeWindow(string challengeUrl, byte[][] qrMatrix)
    {
        InitializeComponent();
        GenerateQrImage(qrMatrix);

        // Subscribe to login success event to auto-close
        Steam3Session.OnLoginSuccess += Steam3Session_OnLoginSuccess;
    }

    private void Steam3Session_OnLoginSuccess()
    {
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            // Window might already be closed, ignore
            System.Diagnostics.Debug.WriteLine($"Error closing QR window: {ex.Message}");
        }
    }

    private void GenerateQrImage(byte[][] qrMatrix)
    {
        if (qrMatrix == null || qrMatrix.Length == 0)
            return;

        int size = qrMatrix.Length;
        int scale = 10; // Scale factor for better visibility
        int quietZone = 4; // Quiet zone around QR code
        int totalSize = (size + quietZone * 2) * scale;

        var bitmap = new WriteableBitmap(totalSize, totalSize, 96, 96, PixelFormats.Bgr32, null);

        bitmap.Lock();
        try
        {
            unsafe
            {
                int* pBackBuffer = (int*)bitmap.BackBuffer;
                int stride = bitmap.BackBufferStride / 4;

                // Fill entire bitmap with white
                for (int y = 0; y < totalSize; y++)
                {
                    for (int x = 0; x < totalSize; x++)
                    {
                        pBackBuffer[y * stride + x] = unchecked((int)0xFFFFFFFF); // White
                    }
                }

                // Draw QR code modules
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        if (qrMatrix[y][x] == 1) // Black module
                        {
                            int startX = (x + quietZone) * scale;
                            int startY = (y + quietZone) * scale;

                            for (int sy = 0; sy < scale; sy++)
                            {
                                for (int sx = 0; sx < scale; sx++)
                                {
                                    int pixelX = startX + sx;
                                    int pixelY = startY + sy;
                                    pBackBuffer[pixelY * stride + pixelX] = unchecked((int)0xFF000000); // Black
                                }
                            }
                        }
                    }
                }
            }

            bitmap.AddDirtyRect(new Int32Rect(0, 0, totalSize, totalSize));
        }
        finally
        {
            bitmap.Unlock();
        }

        QrCodeImage.Source = bitmap;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            // Unsubscribe from event
            Steam3Session.OnLoginSuccess -= Steam3Session_OnLoginSuccess;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error unsubscribing from login success: {ex.Message}");
        }
        base.OnClosed(e);
    }
}
