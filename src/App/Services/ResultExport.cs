using System.Text;
using ClosedXML.Excel;

namespace DocumentTextRecognition.Services;

/// <summary>Файл результата: все строки таблицы (было, стало, статус, причина) в Excel, CSV или текст.</summary>
public static class ResultExport
{
    public enum Format { Excel, Csv, Text }

    public static string Extension(Format f) => f switch { Format.Excel => ".xlsx", Format.Csv => ".csv", _ => ".txt" };

    public static string SuggestedName(DateTime when) => $"Результат {when:yyyy-MM-dd HH-mm}";

    private static readonly string[] Headers = ["Было", "Стало", "Статус", "Причина", "Шаблон"];

    private static string[] Cells(ResultRow r) =>
        [r.Was, r.IsNamed ? r.NewName ?? "" : "", r.StatusText, r.IsNamed ? "" : r.Reason ?? "", r.Template ?? ""];

    public static void Save(string path, IReadOnlyList<ResultRow> rows, Format format)
    {
        switch (format)
        {
            case Format.Excel:
                using (var book = new XLWorkbook())
                {
                    var sheet = book.Worksheets.Add("Результат");
                    for (var c = 0; c < Headers.Length; c++) sheet.Cell(1, c + 1).Value = Headers[c];
                    sheet.Row(1).Style.Font.Bold = true;
                    for (var i = 0; i < rows.Count; i++)
                    {
                        var cells = Cells(rows[i]);
                        for (var c = 0; c < cells.Length; c++) sheet.Cell(i + 2, c + 1).Value = cells[c];
                    }
                    sheet.SheetView.FreezeRows(1);
                    sheet.Columns().AdjustToContents(1, Math.Min(rows.Count + 1, 500));
                    book.SaveAs(path);
                }
                break;
            case Format.Csv:
                // Точка с запятой и UTF-8 с меткой: так файл правильно открывает русский Excel.
                var csv = new StringBuilder();
                csv.AppendLine(string.Join(';', Headers.Select(Quote)));
                foreach (var r in rows) csv.AppendLine(string.Join(';', Cells(r).Select(Quote)));
                File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
                break;
            default:
                var text = new StringBuilder();
                foreach (var r in rows)
                    text.AppendLine(r.IsNamed ? $"{r.Was} -> {r.NewName} ({r.StatusText})" : $"{r.Was}: {r.StatusText}, {r.Reason}");
                File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
                break;
        }
    }

    private static string Quote(string s) => s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
