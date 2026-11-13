using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using DocumentTextRecognition.Common;
using DocumentTextRecognition.Services;
using DocumentTextRecognition.Tests.Samples;

namespace DocumentTextRecognition.AppTests;

/// <summary>Окно программы без экрана: Skia рисует кадры, их можно сохранить картинками.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<global::DocumentTextRecognition.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    private static HeadlessUnitTestSession? _session;
    public static HeadlessUnitTestSession Session => _session ??= HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
}

/// <summary>
/// Окно целиком: разделы открываются, шаблоны проверяются, настоящий прогон на синтетических
/// сканах заполняет таблицу и карточку файла. С переменной DTR_SCREENS кадры сохраняются в
/// эту папку (снимки для проверки оформления).
/// </summary>
public sealed class WindowTests : IDisposable
{
    private readonly Workbench _wb = new();
    private readonly string _data = Path.Combine(Path.GetTempPath(), "dtr-data-" + Guid.NewGuid().ToString("N")[..8]);

    public WindowTests()
    {
        Directory.CreateDirectory(_data);
        Environment.SetEnvironmentVariable("DTR_DATA_DIR", _data);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DTR_DATA_DIR", null);
        _wb.Dispose();
        try { Directory.Delete(_data, true); } catch (IOException) { }
    }

    private static void Shot(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("DTR_SCREENS");
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        if (string.IsNullOrEmpty(dir) || frame is null) return;
        Directory.CreateDirectory(dir);
        frame.Save(Path.Combine(dir, name + ".png"));
    }

    private static async Task Until(Func<bool> done, int seconds)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow > limit) throw new TimeoutException("окно не дождалось результата");
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [OcrFact]
    public async Task Whole_window_first_run_templates_run_and_results()
    {
        _wb.AddTemplate("Чертеж детали", FormKind.Drawing);
        _wb.AddTemplate("Спецификация", FormKind.Specification);
        _wb.AddTemplate("Сборочный чертеж", FormKind.DrawingA3, Marking.Combined);
        _wb.AddTemplate("Схема соединений", FormKind.Drawing, Marking.SeparateNoType);
        _wb.AddDocument("scan_0001.pdf", FormKind.Drawing, "ЮИЛГ.301121.001", "Кронштейн", new ScanOptions { Dpi = 250 }, docType: "Чертеж детали");
        _wb.AddDocument("scan_0002.pdf", FormKind.Specification, "ЮИЛГ.466229.001", "Плата", new ScanOptions { Dpi = 300 });
        _wb.AddDocument("scan_0003.pdf", FormKind.DrawingA3, "ПРМД.301261.010", "Рама сварная", new ScanOptions { Dpi = 250 }, docType: "Сборочный чертеж");
        _wb.AddDocument(Path.Combine("Подпапка", "scan_0004.pdf"), FormKind.Drawing, "ЮИЛГ.741512.002", "Втулка", new ScanOptions { Dpi = 250 }, docType: "Чертеж детали");
        File.WriteAllText(Path.Combine(_wb.Source, "scan_0005.pdf"), "это не PDF");

        await HeadlessApp.Session.Dispatch(async () =>
        {
            // Первый запуск: ничего не выбрано.
            var first = new MainWindow();
            first.Show();
            Assert.True(first.Processing.FindControl<Border>("StartHint")!.IsVisible);
            Assert.False(first.Processing.FindControl<Button>("RunButton")!.IsEnabled);
            Shot(first, "01-first-run");
            first.Close();

            new AppSettings { TemplatesFolder = _wb.Templates, SourceFolder = _wb.Source, Recursive = true, Workers = 2 }.Save();
            var window = new MainWindow();
            window.Show();
            Shot(window, "02-ready");

            window.Show("templates");
            var list = window.Templates.FindControl<ListBox>("List")!;
            await Until(() => list.Items.Cast<TemplateItem>().All(t => t.Recognized is not null), 120);
            var items = list.Items.Cast<TemplateItem>().ToList();
            Assert.Equal(4, items.Count);
            Assert.Equal(3, items.Count(t => t.Recognized == true));
            var old = items.Single(t => t.FileName == "Схема соединений.pdf");
            Assert.Equal("Не распознан: нет желтой рамки (тип документа)", old.Sub);
            Assert.Equal("синяя, зеленая", old.Frames);
            Assert.Equal("A4, книжная", items.Single(t => t.FileName == "Чертеж детали.pdf").PageSize);
            Assert.Equal("красная", items.Single(t => t.FileName == "Сборочный чертеж.pdf").Frames);
            list.SelectedItem = items.Single(t => t.FileName == "Сборочный чертеж.pdf");
            await Task.Delay(1500);
            Shot(window, "03-templates");

            window.Show("processing");
            window.Processing.StartWithoutConfirm();
            await Until(() => !window.Processing.IsRunning, 600);
            await Task.Delay(400);
            var table = window.Processing.FindControl<ListBox>("Table")!;
            var rows = table.Items.Cast<ResultRow>().ToList();
            Assert.Equal(5, rows.Count);
            Assert.Equal(rows.OrderBy(r => r.Was, StringComparer.CurrentCultureIgnoreCase).Select(r => r.Was), rows.Select(r => r.Was));
            Assert.Contains(rows, r => r.NewName == "ЮИЛГ.301121.001 Кронштейн Чертеж детали.pdf");
            Assert.Contains(rows, r => r.NewName == "ЮИЛГ.466229.001 Плата Спецификация.pdf");
            Assert.Contains(rows, r => r.NewName == "ПРМД.301261.010 Рама сварная Сборочный чертеж.pdf");
            Assert.Contains(rows, r => r.Was == "scan_0005.pdf" && r.Kind == StatusKind.Skipped);
            Assert.True(File.Exists(Path.Combine(_wb.Source, "Подпапка", "ЮИЛГ.741512.002 Втулка Чертеж детали.pdf")));
            table.SelectedItem = rows.Single(r => r.Was == "scan_0005.pdf");
            Shot(window, "04-results");

            // Поиск и список статусов работают вместе.
            window.Processing.FindControl<TextBox>("SearchBox")!.Text = "плата";
            Dispatcher.UIThread.RunJobs();
            Assert.Single(table.Items);
            window.Processing.FindControl<TextBox>("SearchBox")!.Text = "";
            Dispatcher.UIThread.RunJobs();
            window.Processing.FindControl<ComboBox>("StatusBox")!.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            Assert.Single(table.Items);
            window.Processing.FindControl<ComboBox>("StatusBox")!.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();

            window.Show("settings");
            Shot(window, "05-settings");
            window.Show("about");
            await Task.Delay(3000);
            Shot(window, "06-about");
            window.Close();
            return 0;
        }, CancellationToken.None);
    }
}
