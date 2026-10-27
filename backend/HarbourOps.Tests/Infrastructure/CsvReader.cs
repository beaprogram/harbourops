using System.Text;

namespace HarbourOps.Tests.Infrastructure;

/// <summary>A small RFC 4180 reader so tests can check exported files the way a spreadsheet would read them.</summary>
public static class CsvReader
{
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') { inQuotes = false; }
                else { cell.Append(c); }
            }
            else if (c == '"') { inQuotes = true; }
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                row.Add(cell.ToString()); cell.Clear();
                rows.Add([.. row]); row.Clear();
                i++;
            }
            else { cell.Append(c); }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add([.. row]);
        }

        return rows;
    }
}
