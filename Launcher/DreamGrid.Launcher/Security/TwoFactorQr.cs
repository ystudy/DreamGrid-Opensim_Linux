using QRCoder;

namespace DreamGrid.Launcher.Security;

/// <summary>
/// The QR code an authenticator app scans to set up two-factor sign-in, made here (no outside image service) as SVG markup to
/// put straight into the page. It sizes by its viewBox (the page's CSS sets the width) and carries colours as attributes, never
/// as <c>style</c>, so a <c>style-src 'self'</c> policy allows it.
/// </summary>
public static class TwoFactorQr
{
    public static string Svg(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(4, "#000000", "#ffffff", drawQuietZones: true, SvgQRCode.SizingMode.ViewBoxAttribute);
    }
}
