using System.Text;
using QRCoder;

namespace SMSManagement.Modules.Coupon.Services;

/// <summary>
/// Renders a coupon's real code as a scannable barcode, server-side, as an
/// SVG string (vector — scales + prints crisply, no raster library, no native
/// dependency). The brand picks the format.
///   "qr"      — QR code (via QRCoder's SVG output).
///   "code128" — Code 128 set B, hand-rolled SVG from the standard pattern
///               table. Covers POS scanners for retail coupon codes.
/// An unknown format falls back to QR.
/// </summary>
public interface IBarcodeService
{
    /// <summary>Returns an &lt;svg&gt; document string for the given value.</summary>
    string RenderSvg(string format, string value);
}

public sealed class BarcodeService : IBarcodeService
{
    public string RenderSvg(string format, string value)
    {
        value ??= string.Empty;
        return (format ?? "qr").Trim().ToLowerInvariant() switch
        {
            "code128" => RenderCode128(value),
            _         => RenderQr(value)
        };
    }

    // ---------------- QR ----------------

    private static string RenderQr(string value)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(
            string.IsNullOrEmpty(value) ? " " : value, QRCodeGenerator.ECCLevel.M);
        // SvgQRCode emits a self-contained <svg>; viewBox lets the page scale it.
        return new SvgQRCode(data).GetGraphic(4);
    }

    // ---------------- Code 128 set B ----------------

    // value 0-106 → 6-element bar/space widths (stop, 106, is 7 elements).
    private static readonly string[] Patterns =
    {
        "212222","222122","222221","121223","121322","131222","122213","122312","132212","221213",
        "221312","231212","112232","122132","122231","113222","123122","123221","223211","221132",
        "221231","213212","223112","312131","311222","321122","321221","312212","322112","322211",
        "212123","212321","232121","111323","131123","131321","112313","132113","132311","211313",
        "231113","231311","112133","112331","132131","113123","113321","133121","313121","211331",
        "231131","213113","213311","213131","311123","311321","331121","312113","312311","332111",
        "314111","221411","431111","111224","111422","121124","121421","141122","141221","112214",
        "112412","122114","122411","142112","142211","241211","221114","413111","241112","134111",
        "111242","121142","121241","114212","124112","124211","411212","421112","421211","212141",
        "214121","412121","111143","111341","131141","114113","114311","411113","411311","113141",
        "114131","311141","411131","211412","211214","211232","2331112"
    };
    private const int StartB = 104;
    private const int Stop = 106;

    private static string RenderCode128(string value)
    {
        // Code set B covers ASCII 32-126; substitute anything outside.
        var clean = new string((value ?? string.Empty)
            .Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());
        if (clean.Length == 0) clean = " ";

        var codes = new List<int> { StartB };
        foreach (var c in clean) codes.Add(c - 32);

        // Modulo-103 checksum: start + Σ(position × value), position from 1.
        long sum = StartB;
        for (var i = 0; i < clean.Length; i++) sum += (long)(i + 1) * (clean[i] - 32);
        codes.Add((int)(sum % 103));
        codes.Add(Stop);

        // Each pattern digit is a run length; bars at even index, spaces at odd.
        var sb = new StringBuilder();
        var x = 0;
        const int unit = 2;     // module width in SVG px
        const int height = 90;
        foreach (var code in codes)
        {
            var pat = Patterns[code];
            for (var i = 0; i < pat.Length; i++)
            {
                var w = (pat[i] - '0') * unit;
                if (i % 2 == 0) // bar
                    sb.Append($"<rect x=\"{x}\" y=\"0\" width=\"{w}\" height=\"{height}\" fill=\"#000\"/>");
                x += w;
            }
        }
        var totalW = x;
        // 10-module quiet zone each side.
        var quiet = 10 * unit;
        var svgW = totalW + quiet * 2;
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {svgW} {height}\" " +
               $"width=\"100%\" preserveAspectRatio=\"xMidYMid meet\">" +
               $"<rect width=\"{svgW}\" height=\"{height}\" fill=\"#fff\"/>" +
               $"<g transform=\"translate({quiet},0)\">{sb}</g></svg>";
    }
}
