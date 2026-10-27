namespace HarbourOps.Api.Features.WorkOrders.Export;

/// <summary>
/// Two separate jobs, kept in separate functions on purpose:
///
/// 1. <see cref="Escape"/> is CSV *syntax* (RFC 4180). It makes a value survive the file format:
///    commas, quotes, and line breaks stay inside their cell. It changes nothing a reader would see.
///
/// 2. <see cref="NeutralizeFormula"/> is a *spreadsheet safety* measure against formula injection
///    (also called CSV injection). Excel, LibreOffice, and Google Sheets treat a cell that starts with
///    = + - @ (or certain control characters) as a formula, so a work-order title like
///    <c>=HYPERLINK("http://evil.example","Click")</c> could run when a manager opens the export.
///    Prefixing such cells with an apostrophe makes the app treat them as text. Correct escaping
///    alone does not prevent this, because the formula is perfectly valid CSV.
///
/// Every cell is neutralised first, then escaped.
/// </summary>
public static class Csv
{
    // ASCII triggers from the OWASP CSV-injection guidance, plus their full-width forms, which some
    // spreadsheet apps normalise to ASCII before evaluating.
    private static readonly HashSet<char> FormulaTriggers = ['=', '+', '-', '@', '＝', '＋', '－', '＠'];

    public static string Cell(string? value) => Escape(NeutralizeFormula(value ?? string.Empty));

    /// <summary>
    /// Prefixes a single quote when a spreadsheet could read the cell as a formula:
    ///  • it starts with a trigger character (= + - @, including full-width forms);
    ///  • it starts with a control character such as tab or carriage return (OWASP lists these, and
    ///    some apps strip them and then evaluate what follows);
    ///  • it starts with whitespace and the first visible character is a trigger ("  =1+1"), because
    ///    some apps trim leading spaces before deciding.
    /// </summary>
    public static string NeutralizeFormula(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var first = value[0];
        if (FormulaTriggers.Contains(first) || char.IsControl(first))
        {
            return "'" + value;
        }

        if (char.IsWhiteSpace(first))
        {
            foreach (var c in value)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    continue;
                }

                return FormulaTriggers.Contains(c) ? "'" + value : value;
            }
        }

        return value;
    }

    /// <summary>
    /// RFC 4180 quoting: wrap in double quotes when the value contains a comma, double quote, CR, or LF
    /// (or has leading/trailing spaces some readers would trim), and double any embedded quotes.
    /// </summary>
    public static string Escape(string value)
    {
        var needsQuotes = value.Length > 0
            && (value.AsSpan().IndexOfAny(",\"\r\n") >= 0 || value[0] == ' ' || value[^1] == ' ');
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>One CSV record. Records end with CRLF, as RFC 4180 specifies.</summary>
    public static string Row(IEnumerable<string?> cells) => string.Join(',', cells.Select(Cell)) + "\r\n";
}
