using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Dialogs;
using DocumentTextRecognition.Services;
using DocumentTextRecognition.Tests.Samples;

namespace DocumentTextRecognition.AppTests;

public sealed class TemplateEditorTests
{
    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
    }

    private static Point InWindow(Window window, Control control, double x, double y) =>
        control.TranslatePoint(new Point(x, y), window)!.Value;

    private static void Drag(Window window, TemplateRegionSurface surface, Point from, Point to)
    {
        window.MouseDown(InWindow(window, surface, from.X, from.Y), MouseButton.Left);
        window.MouseMove(InWindow(window, surface, to.X, to.Y), RawInputModifiers.LeftMouseButton);
        window.MouseUp(InWindow(window, surface, to.X, to.Y), MouseButton.Left);
        Layout(window);
    }

    private static async Task Until(Func<bool> predicate)
    {
        var limit = DateTime.UtcNow.AddSeconds(90);
        while (!predicate())
        {
            if (DateTime.UtcNow > limit) throw new TimeoutException("редактор не дождался загрузки PDF");
            await Task.Delay(40);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Shot(Window window, string name)
    {
        Layout(window);
        var folder = Environment.GetEnvironmentVariable("DTR_SCREENS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(folder, name + ".png"));
    }

    [Fact]
    public async Task Mouse_draw_move_resize_and_cancel_preserve_page_coordinates_after_zoom()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var surface = new TemplateRegionSurface { Width = 400, Height = 300, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var window = new Window { Width = 600, Height = 500, Content = surface };
            window.Show();
            Layout(window);
            surface.DrawingRole = FieldRole.Designation;
            Drag(window, surface, new Point(40, 30), new Point(160, 90));
            var drawn = Assert.Single(surface.Regions).Bounds;
            Assert.Equal(.1, drawn.X, 8);
            Assert.Equal(.1, drawn.Y, 8);
            Assert.Equal(.3, drawn.Width, 8);
            Assert.Equal(.2, drawn.Height, 8);

            surface.Width = 800;
            surface.Height = 600;
            window.Width = 1000;
            window.Height = 800;
            Layout(window);
            Assert.Equal(800, surface.Bounds.Width);
            Drag(window, surface, new Point(200, 120), new Point(280, 180));
            var moved = Assert.Single(surface.Regions).Bounds;
            Assert.Equal(.2, moved.X, 8);
            Assert.Equal(.2, moved.Y, 8);
            Assert.Equal(.3, moved.Width, 8);
            Assert.Equal(.2, moved.Height, 8);

            Drag(window, surface, new Point(400, 240), new Point(480, 300));
            var resized = Assert.Single(surface.Regions).Bounds;
            Assert.Equal(.4, resized.Width, 8);
            Assert.Equal(.3, resized.Height, 8);

            // Escape in the middle of replacing an existing region restores it, including its role.
            surface.DrawingRole = FieldRole.Designation;
            window.MouseDown(InWindow(window, surface, 40, 30), MouseButton.Left);
            window.MouseMove(InWindow(window, surface, 100, 100), RawInputModifiers.LeftMouseButton);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.MouseUp(InWindow(window, surface, 100, 100), MouseButton.Left);
            Assert.False(surface.IsDragging);
            Assert.Equal(resized, Assert.Single(surface.Regions).Bounds);

            // Clicking without dragging must not destroy the old field either.
            surface.DrawingRole = FieldRole.Designation;
            Drag(window, surface, new Point(40, 30), new Point(40, 30));
            Assert.Equal(resized, Assert.Single(surface.Regions).Bounds);
            surface.Select(0);
            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            Assert.Empty(surface.Regions);
            window.Close();
            return 0;
        }, CancellationToken.None);
    }

    [OcrTheory]
    [InlineData(FormKind.Specification, false, "АБВГ.466229.001", "Плата", null)]
    [InlineData(FormKind.Drawing, false, "ЮИЛГ.461411.123", "Корпус", "Сборочный чертеж")]
    [InlineData(FormKind.DrawingA3, true, "ПРМД.301261.010", "Рама сварная", "Сборочный чертеж")]
    public async Task Mouse_drawn_regions_are_saved_reopened_and_used_by_both_engines_on_same_pdf(
        FormKind kind, bool combined, string designation, string name, string? documentType)
    {
        using var wb = new Workbench();
        var fileName = kind == FormKind.Specification ? "Спецификация без рамок.pdf" : $"{kind} без рамок.pdf";
        var file = wb.AddDocument(fileName, kind, designation, name, docType: documentType);
        var pdfBytes = File.ReadAllBytes(file);
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var owner = new Window { Width = 1180, Height = 760 };
            owner.Show();
            var editor = new TemplateEditorDialog(file);
            editor.Show(owner);
            Layout(editor);
            var surface = Named<TemplateRegionSurface>(editor, "RegionSurface");
            await Until(() => surface.IsEnabled && surface.Width > 0);
            Assert.Empty(surface.Regions);
            Assert.False(Named<Button>(editor, "SaveTemplate").IsEnabled);

            Named<ComboBox>(editor, "ZoomSelector").SelectedIndex = 0;
            Layout(editor);
            var fields = SyntheticForms.Fields(kind);
            var (widthMm, heightMm) = SyntheticForms.PageSizeMm(kind);
            void Draw(FieldRole role, RectMm mm)
            {
                Named<Button>(editor, "Draw" + role).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Drag(editor, surface, new Point(mm.X / widthMm * surface.Bounds.Width, mm.Y / heightMm * surface.Bounds.Height),
                    new Point((mm.X + mm.Width) / widthMm * surface.Bounds.Width, (mm.Y + mm.Height) / heightMm * surface.Bounds.Height));
            }
            if (combined) Draw(FieldRole.Combined, fields.Both.Inflate(-1));
            else
            {
                Draw(FieldRole.Designation, fields.Designation.Inflate(-1));
                Draw(FieldRole.Name, fields.Name.Inflate(-1));
                if (documentType is not null) Draw(FieldRole.Type, fields.Type.Inflate(-1));
            }
            var typeAbsent = documentType is null;
            Named<CheckBox>(editor, "TypeAbsent").IsChecked = typeAbsent;
            Layout(editor);
            var regionCount = combined ? 1 : typeAbsent ? 2 : 3;
            Assert.Equal(regionCount, surface.Regions.Count);
            Assert.Equal(regionCount, Named<ListBox>(editor, "RegionCoordinates").ItemCount);
            Assert.True(Named<Button>(editor, "SaveTemplate").IsEnabled);
            var expected = surface.Regions;
            Named<Button>(editor, "SaveTemplate").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !editor.IsVisible);

            var stored = TemplateMarkupStore.Load(file);
            Assert.Null(stored.Problem);
            Assert.Equal(typeAbsent, stored.Markup!.TypeAbsent);
            Assert.Equal(expected, stored.Markup.Regions);
            Assert.Equal(pdfBytes, File.ReadAllBytes(file));

            var item = new TemplateItem { FilePath = file };
            TemplateCatalog.Inspect(item);
            Assert.True(item.Recognized);
            Assert.Contains("области сохранены", item.Sub);
            var clean = TemplateCatalog.RenderPng(file, 0);
            var preview = TemplateCatalog.RenderPng(file, 0, showRegions: true);
            Assert.NotNull(clean);
            Assert.NotNull(preview);
            Assert.False(clean.SequenceEqual(preview));

            var reopened = new TemplateEditorDialog(file);
            reopened.Show(owner);
            Layout(reopened);
            var secondSurface = Named<TemplateRegionSurface>(reopened, "RegionSurface");
            await Until(() => secondSurface.IsEnabled && secondSurface.Width > 0);
            Assert.Equal(expected, secondSurface.Regions);
            Assert.Equal(typeAbsent, Named<CheckBox>(reopened, "TypeAbsent").IsChecked);
            if (kind == FormKind.Specification)
            {
                Named<ScrollViewer>(reopened, "PageScroll").Offset = new Vector(0, 1000);
                Shot(reopened, "07-template-editor");
                reopened.Width = 800;
                reopened.Height = 650;
                Layout(reopened);
                Named<ScrollViewer>(reopened, "PageScroll").Offset = new Vector(0, 1000);
                Shot(reopened, "08-template-editor-small");
            }
            reopened.Close();
            owner.Close();

            // The same entry point as the check dialog loads the sidecar afresh. The PDF
            // used as template is exactly the PDF whose text is being recognized here.
            var report = await Task.Run(() => CheckScanDialog.Recognize(wb.Templates, file, file));
            Assert.True(report.Result.Values is not null, report.Result.Problem);
            var values = new FieldValues(designation, name, documentType ?? "Спецификация");
            Assert.Equal(values, report.Result.Values);
            var proposed = FileNames.Build(report.Result.Values!);
            Assert.Null(proposed.Problem);
            Assert.Equal($"{designation} {name} {values.Type}.pdf", proposed.Name);
            var readFields = report.Diagnostics.Where(d => d.Field is not null).Select(d => d.Field!).ToArray();
            Assert.Equal(regionCount, readFields.Length);
            foreach (var region in expected)
            {
                var read = Assert.Single(readFields, f => f.Role == region.Role);
                Assert.Equal(region.Bounds, read.Bounds);
                Assert.NotEmpty(read.CropPng);
                Assert.Equal("Tesseract", read.First.Engine);
                Assert.Equal("PaddleOCR", read.Second.Engine);
                Assert.Null(read.First.Problem);
                Assert.Null(read.Second.Problem);
                if (region.Role == FieldRole.Combined)
                {
                    foreach (var engine in new[] { read.First, read.Second })
                    {
                        Assert.Contains(designation, engine.ParsedText);
                        Assert.Contains(name, engine.ParsedText);
                        Assert.Contains(values.Type, engine.ParsedText);
                    }
                }
                else
                {
                    var expectedText = region.Role switch
                    {
                        FieldRole.Designation => designation,
                        FieldRole.Name => name,
                        _ => values.Type,
                    };
                    Assert.Equal(expectedText, read.First.ParsedText);
                    Assert.Equal(expectedText, read.Second.ParsedText);
                }
            }
            Assert.Equal(pdfBytes, File.ReadAllBytes(file));
            RecordSamePdfCheck(file, pdfBytes, kind, expected, stored.Markup, report, proposed.Name!);
            return 0;
        }, CancellationToken.None);
    }

    private static void RecordSamePdfCheck(string file, byte[] original, FormKind kind,
        IReadOnlyList<TemplateRegion> drawn, TemplateMarkup saved, CheckScanDialog.CheckReport report, string proposedName)
    {
        var output = Environment.GetEnvironmentVariable("DTR_EDITOR_TEST_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        var folder = Path.Combine(output, kind.ToString());
        Directory.CreateDirectory(folder);
        File.Copy(file, Path.Combine(folder, "document.pdf"), overwrite: true);
        File.Copy(TemplateMarkupStore.PathFor(file), Path.Combine(folder, "document.pdf.template.json"), overwrite: true);
        var record = new
        {
            Test = "Mouse_drawn_regions_are_saved_reopened_and_used_by_both_engines_on_same_pdf",
            VerifiedUtc = DateTimeOffset.UtcNow,
            DocumentKind = kind.ToString(),
            SamePdfForEditorAndRecognition = true,
            OriginalPdfSha256 = Convert.ToHexString(SHA256.HashData(original)),
            PdfAfterRecognitionSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),
            saved.PageIndex,
            saved.PageWidth,
            saved.PageHeight,
            saved.TypeAbsent,
            CoordinatesDrawnByMouse = drawn.Select(r => new { Role = r.Role.ToString(), r.Bounds }),
            CoordinatesReloadedFromDisk = saved.Regions.Select(r => new { Role = r.Role.ToString(), r.Bounds }),
            CropSettings = new { Dpi = FieldCrop.OcrDpi, AlignmentMarginMm = FieldCrop.MarginMm },
            Fields = report.Result.Values,
            ProposedName = proposedName,
            ReadFields = report.Diagnostics.Where(d => d.Field is not null).Select(d =>
            {
                var field = d.Field!;
                var crop = field.Role + ".png";
                File.WriteAllBytes(Path.Combine(folder, crop), field.CropPng);
                return new { d.PageNumber, Role = field.Role.ToString(), field.Bounds, Crop = crop, field.First, field.Second };
            }).ToArray(),
            Result = "passed",
        };
        File.WriteAllText(Path.Combine(folder, "verification.json"), JsonSerializer.Serialize(record,
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
    }
}
