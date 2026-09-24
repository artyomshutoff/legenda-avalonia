using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ZXing;
using ZXing.QrCode;

namespace Legenda.App;

public static class ClientQrCode
{
    public static byte[] CreatePng(string membershipNumber)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions { Width = 768, Height = 768, Margin = 4 }
        };
        var pixels = writer.Write(membershipNumber.Replace(" ", ""));
        using var bitmap = new WriteableBitmap(new PixelSize(pixels.Width, pixels.Height),
            new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var buffer = bitmap.Lock())
            for (var y = 0; y < pixels.Height; y++)
                Marshal.Copy(pixels.Pixels, y * pixels.Width * 4,
                    buffer.Address + y * buffer.RowBytes, pixels.Width * 4);
        using var output = new MemoryStream();
        bitmap.Save(output);
        return output.ToArray();
    }
}
