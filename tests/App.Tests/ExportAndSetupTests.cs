using System.IO.Compression;
using ClosedXML.Excel;
using DocumentTextRecognition.Common;
using DocumentTextRecognition.Services;

namespace DocumentTextRecognition.AppTests;

public sealed class ExportAndSetupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dtr-app-" + Guid.NewGuid().ToString("N")[..8]);

    public ExportAndSetupTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static List<ResultRow> Rows() =>
    [
        new("scan_0001.pdf", "АБВГ.301121.001 Кронштейн Сборочный чертеж.pdf", null, StatusKind.Renamed, "/x/a.pdf", "Чертеж"),
        new("scan_0002.pdf", null, "имя «X» уже занято; другим файлом", StatusKind.Skipped, "/x/b.pdf"),
        new("scan_0003.pdf", null, "ошибка обработки: недостаточно памяти", StatusKind.Error, "/x/c.pdf"),
    ];

    [Fact]
    public void Csv_has_header_semicolons_and_quotes()
    {
        var path = Path.Combine(_dir, "r.csv");
        ResultExport.Save(path, Rows(), ResultExport.Format.Csv);
        var lines = File.ReadAllLines(path);
        Assert.Equal("Было;Стало;Статус;Причина;Шаблон", lines[0]);
        Assert.Equal("scan_0001.pdf;АБВГ.301121.001 Кронштейн Сборочный чертеж.pdf;Переименован;;Чертеж", lines[1]);
        Assert.Equal("scan_0002.pdf;;Пропущен;\"имя «X» уже занято; другим файлом\";", lines[2]);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path)[..3]);
    }

    [Fact]
    public void Excel_and_text_contain_all_rows()
    {
        var xlsx = Path.Combine(_dir, "r.xlsx");
        ResultExport.Save(xlsx, Rows(), ResultExport.Format.Excel);
        using (var book = new XLWorkbook(xlsx))
        {
            var sheet = book.Worksheet(1);
            Assert.Equal("Статус", sheet.Cell(1, 3).GetString());
            Assert.Equal("Ошибка", sheet.Cell(4, 3).GetString());
        }
        var txt = Path.Combine(_dir, "r.txt");
        ResultExport.Save(txt, Rows(), ResultExport.Format.Text);
        Assert.Equal(3, File.ReadAllLines(txt).Length);
        Assert.StartsWith("Результат 2026-11-13 10-42", ResultExport.SuggestedName(new DateTime(2026, 11, 13, 10, 42, 0)));
    }

    [Fact]
    public void Setup_payload_is_appended_and_installed_with_folder_swap()
    {
        var program = Path.Combine(_dir, "program");
        Directory.CreateDirectory(Path.Combine(program, "ocr", "paddle"));
        File.WriteAllText(Path.Combine(program, AppInfo.ExeName), "exe v2");
        File.WriteAllText(Path.Combine(program, "ocr", "paddle", "model.bin"), "model");
        var zip = Path.Combine(_dir, "payload.zip");
        ZipFile.CreateFromDirectory(program, zip);
        var setup = Path.Combine(_dir, AppInfo.SetupName);
        File.WriteAllBytes(setup, [0x4D, 0x5A, 1, 2, 3]);
        Installation.AppendPayload(setup, zip);

        Assert.Equal(11, Installation.UnpackedSize(setup));
        var install = Path.Combine(_dir, "Установлено", AppInfo.Title);
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, AppInfo.ExeName), "exe v1");
        File.WriteAllText(Path.Combine(install, "old.dll"), "old");

        var steps = new List<double>();
        Installation.Install(setup, install, (p, _) => steps.Add(p), CancellationToken.None);

        Assert.Equal("exe v2", File.ReadAllText(Path.Combine(install, AppInfo.ExeName)));
        Assert.True(File.Exists(Path.Combine(install, "ocr", "paddle", "model.bin")));
        Assert.False(File.Exists(Path.Combine(install, "old.dll")));
        Assert.False(Directory.Exists(install + ".old"));
        Assert.False(Directory.Exists(install + ".new"));
        Assert.Equal(1.0, steps[^1], 3);
    }

    [Fact]
    public void Setup_without_payload_is_reported()
    {
        var setup = Path.Combine(_dir, "empty-Setup.exe");
        File.WriteAllBytes(setup, new byte[64]);
        Assert.Null(Installation.Payload(setup));
        Assert.Throws<InvalidDataException>(() => Installation.UnpackedSize(setup));
    }

    [Theory]
    [InlineData("1.1.0", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("0.9.5", "1.0.0", false)]
    [InlineData("1.0.1+abc", "1.0.0", true)]
    public void Update_version_comparison(string available, string current, bool newer) =>
        Assert.Equal(newer, UpdateSource.IsNewer(UpdateSource.Parse(available), current));

    [Fact]
    public void Missing_update_folder_gives_no_version() =>
        Assert.Null(UpdateSource.Available(Path.Combine(_dir, "нет такой папки")));

    [Fact]
    public void Settings_survive_save_and_load()
    {
        Environment.SetEnvironmentVariable("DTR_DATA_DIR", _dir);
        try
        {
            var s = new AppSettings { TemplatesFolder = @"\\fileserver\КД\Шаблоны", Workers = 2, NumberWhenTaken = true, FontScale = 125 };
            s.Save();
            var loaded = AppSettings.Load();
            Assert.Equal(@"\\fileserver\КД\Шаблоны", loaded.TemplatesFolder);
            Assert.Equal((2, true, 125), (loaded.Workers, loaded.NumberWhenTaken, loaded.FontScale));
            Assert.Contains("Шаблоны", File.ReadAllText(AppSettings.FilePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DTR_DATA_DIR", null);
        }
    }

    [Theory]
    [InlineData(1, "1 PDF-файл")]
    [InlineData(3, "3 PDF-файла")]
    [InlineData(11, "11 PDF-файлов")]
    [InlineData(248, "248 PDF-файлов")]
    [InlineData(22, "22 PDF-файла")]
    public void Pdf_count_words(int n, string text) => Assert.Equal(text, Pages.ProcessingPage.PdfCount(n));
}
