using System;
using DeviceSync.Application;
using QRCoder;

namespace DeviceSync.Infrastructure;

public sealed class QrCodeGenerator : IQrCodeGenerator
{
    private const int MinimumProductionPixels = 768;

    public byte[] GeneratePng(string content, int pixelsPerModule)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data);
        var modulePixels = Math.Max(1, pixelsPerModule);
        byte[] image;
        do
        {
            image = png.GetGraphic(modulePixels);
            if (ReadPngSize(image).Width >= MinimumProductionPixels)
            {
                return image;
            }

            modulePixels++;
        }
        while (modulePixels <= 64);

        return image;
    }

    private static (int Width, int Height) ReadPngSize(byte[] png)
    {
        return (ReadBigEndianInt32(png, 16), ReadBigEndianInt32(png, 20));
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset)
    {
        return (bytes[offset] << 24) |
            (bytes[offset + 1] << 16) |
            (bytes[offset + 2] << 8) |
            bytes[offset + 3];
    }
}
