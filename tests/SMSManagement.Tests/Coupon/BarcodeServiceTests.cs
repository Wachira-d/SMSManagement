using FluentAssertions;
using SMSManagement.Modules.Coupon.Services;

namespace SMSManagement.Tests.Coupon;

public sealed class BarcodeServiceTests
{
    private readonly BarcodeService _svc = new();

    [Fact]
    public void Qr_renders_an_svg()
    {
        var svg = _svc.RenderSvg("qr", "HELLO-123");
        svg.Should().Contain("<svg");
        svg.Should().Contain("</svg>");
    }

    [Fact]
    public void Code128_renders_an_svg_with_bars()
    {
        var svg = _svc.RenderSvg("code128", "ABC12345");
        svg.Should().StartWith("<svg");
        svg.Should().Contain("<rect");          // at least one bar
        svg.Should().Contain("viewBox");
    }

    [Fact]
    public void Unknown_format_falls_back_to_qr()
    {
        var svg = _svc.RenderSvg("does-not-exist", "X");
        svg.Should().Contain("<svg");
    }

    [Fact]
    public void Empty_value_does_not_throw()
    {
        _svc.Invoking(s => s.RenderSvg("code128", ""))
            .Should().NotThrow();
        _svc.Invoking(s => s.RenderSvg("qr", ""))
            .Should().NotThrow();
    }

    [Fact]
    public void Code128_substitutes_out_of_range_characters()
    {
        // A non-ASCII char (Thai) must not throw — it's replaced with '?'.
        _svc.Invoking(s => s.RenderSvg("code128", "คูปอง"))
            .Should().NotThrow();
    }
}
