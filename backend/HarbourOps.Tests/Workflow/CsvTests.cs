using HarbourOps.Api.Features.WorkOrders.Export;

namespace HarbourOps.Tests.Workflow;

public sealed class CsvTests
{
    // ---- RFC 4180 escaping: keeps values intact inside the file format ----

    [Theory]
    [InlineData("Leaking tap", "Leaking tap")]
    [InlineData("Sink, kitchen", "\"Sink, kitchen\"")]
    [InlineData("The \"big\" leak", "\"The \"\"big\"\" leak\"")]
    [InlineData("Line one\nLine two", "\"Line one\nLine two\"")]
    [InlineData("Line one\r\nLine two", "\"Line one\r\nLine two\"")]
    [InlineData(" padded ", "\" padded \"")]
    [InlineData("Café déjà vu ✓ 水漏れ 🚰", "Café déjà vu ✓ 水漏れ 🚰")]
    [InlineData("", "")]
    public void Escaping_quotes_only_when_the_format_requires_it(string value, string expected)
    {
        Assert.Equal(expected, Csv.Escape(value));
    }

    [Fact]
    public void Rows_keep_empty_cells_and_end_with_crlf()
    {
        Assert.Equal("a,,\"b,c\"\r\n", Csv.Row(["a", null, "b,c"]));
    }

    // ---- Formula-injection mitigation: a separate concern from escaping ----

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+cmd|' /C calc'!A0")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1:A9)")]
    [InlineData("＝HYPERLINK(\"http://evil.example\")")] // full-width equals sign
    [InlineData("\t=1+1")]                              // leading tab
    [InlineData("\r=1+1")]                              // leading carriage return
    [InlineData("\n@x")]                                // leading line feed
    [InlineData("\u0000=1")]                            // leading control character
    [InlineData("   =1+1")]                             // leading spaces some apps trim
    [InlineData("  +1")]                      // non-breaking and em spaces
    [InlineData(" \t -1")]                              // mixed whitespace and control
    public void Values_a_spreadsheet_could_evaluate_get_a_leading_apostrophe(string value)
    {
        Assert.Equal("'" + value, Csv.NeutralizeFormula(value));
    }

    [Theory]
    [InlineData("WO-12")]
    [InlineData("2026-10-24T17:08:00-03:00")]
    [InlineData("Pipe - kitchen")]
    [InlineData("tenant@harbourview.example")]
    [InlineData("100% fixed")]
    [InlineData("   ")]
    [InlineData("")]
    public void Ordinary_values_are_left_alone(string value)
    {
        Assert.Equal(value, Csv.NeutralizeFormula(value));
    }

    [Fact]
    public void A_cell_is_neutralised_first_and_then_escaped()
    {
        Assert.Equal("\"'=HYPERLINK(\"\"http://evil.example\"\",\"\"Click\"\")\"",
            Csv.Cell("=HYPERLINK(\"http://evil.example\",\"Click\")"));
    }
}
