using QRCoder;

namespace Deployer;

public static class Qr
{
    public static string Svg(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(10, "#000000", "#ffffff", drawQuietZones: true,
            sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    /// <summary>Compact half-block QR for terminals (dark background: light modules drawn as blocks).</summary>
    public static string Ascii(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.L);
        return new AsciiQRCode(data).GetGraphicSmall(drawQuietZones: true, invert: false);
    }
}
