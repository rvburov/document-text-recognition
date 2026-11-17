using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DocumentTextRecognition.Common;
using DocumentTextRecognition.Services;

namespace DocumentTextRecognition.AppTests;

/// <summary>Настоящие строки и окно, но без OCR: проверяет стоимость доставки и показа результатов.</summary>
public sealed class ResultsPerformanceTests : IDisposable
{
    private const int RowCount = 20_000;
    private const int BatchLimit = 256;
    private readonly string _data = Path.Combine(Path.GetTempPath(), "dtr-performance-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousData = Environment.GetEnvironmentVariable("DTR_DATA_DIR");

    public ResultsPerformanceTests()
    {
        Directory.CreateDirectory(_data);
        Environment.SetEnvironmentVariable("DTR_DATA_DIR", _data);
        new AppSettings { CheckUpdates = false, NotifyWhenDone = false }.Save();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DTR_DATA_DIR", _previousData);
        try { Directory.Delete(_data, true); } catch (IOException) { }
    }

    private static ResultRow Row(int index, StatusKind? kind = null)
    {
        var status = kind ?? (StatusKind)(index % 4);
        var named = status is StatusKind.Renamed or StatusKind.AlreadyNamed;
        return new ResultRow($"scan_{index:D5}.pdf",
            named ? $"АБВГ.000000.{index:D5} Деталь {index:D5} Спецификация.pdf" : null,
            named ? null : $"Причина для {index:D5}", status,
            Path.Combine(Path.GetTempPath(), $"scan_{index:D5}.pdf"));
    }

    private static void Render(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
    }

    private static int Realized(ListBox table) => table.GetVisualDescendants().OfType<ListBoxItem>().Count();

    private static async Task<double> Heartbeat()
    {
        var watch = Stopwatch.StartNew();
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => signal.SetResult(), DispatcherPriority.Input);
        await signal.Task;
        return watch.Elapsed.TotalMilliseconds;
    }

    [Fact]
    public void RunSession_default_drain_is_bounded_and_preserves_every_row_in_order()
    {
        var session = new RunSession();
        var sink = (IRunSinkPublic)session;
        const int count = BatchLimit * 4 + 1;
        var expected = Enumerable.Range(0, count).Select(i => Row(i, StatusKind.Renamed)).ToArray();
        foreach (var row in expected) sink.File(row);
        var delivered = new List<ResultRow>();
        while (session.HasPendingRows)
        {
            var batch = session.Drain();
            Assert.InRange(batch.Count, 1, BatchLimit);
            delivered.AddRange(batch);
        }
        Assert.Equal(expected, delivered);
        Assert.Empty(session.Drain());
    }

    [Fact]
    public async Task Completion_flushes_pending_batches_and_yields_before_all_results_are_published()
    {
        const int count = BatchLimit * 4 + 1;
        var rows = Enumerable.Range(0, count).Select(i => Row(i, StatusKind.Renamed)).ToArray();
        var session = new RunSession();
        var sink = (IRunSinkPublic)session;
        sink.Found(count);
        foreach (var row in rows.Reverse()) sink.File(row);

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var window = new MainWindow();
            window.Show();
            Render(window);
            var page = window.Processing;
            page.BeginSession(session);
            try
            {
                var table = page.FindControl<ListBox>("Table")!;
                var source = Assert.IsAssignableFrom<INotifyCollectionChanged>(table.ItemsSource);
                var delivered = new List<ResultRow>();
                var batches = new List<int>();
                source.CollectionChanged += (_, e) =>
                {
                    if (e.Action != NotifyCollectionChangedAction.Add) return;
                    batches.Add(e.NewItems!.Count);
                    delivered.AddRange(e.NewItems.Cast<ResultRow>());
                };
                var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var rowsAtHeartbeat = -1;
                var runningAtHeartbeat = false;
                Dispatcher.UIThread.Post(() =>
                {
                    rowsAtHeartbeat = table.ItemCount;
                    runningAtHeartbeat = page.IsRunning;
                    heartbeat.SetResult();
                }, DispatcherPriority.Input);

                // No Pump call beforehand: completion itself must flush more than one batch.
                await page.CompleteSessionAsync();
                await heartbeat.Task;
                Assert.True(runningAtHeartbeat);
                Assert.InRange(rowsAtHeartbeat, 1, count - 1);
                Assert.False(session.HasPendingRows);
                Assert.False(page.IsRunning);
                Assert.Equal(count, table.ItemCount);
                Assert.Equal(rows.Reverse(), delivered);
                Assert.Equal(rows, table.Items.Cast<ResultRow>());
                Assert.All(batches, batch => Assert.InRange(batch, 1, BatchLimit));
                Assert.True(batches.Count >= 5);
                return 0;
            }
            finally
            {
                if (page.IsRunning) await page.CompleteSessionAsync();
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Twenty_thousand_results_keep_virtualized_rows_selection_search_and_dispatcher_working()
    {
        var session = new RunSession();
        var sink = (IRunSinkPublic)session;
        var rows = Enumerable.Range(0, RowCount).Select(i => Row(i)).ToArray();
        var enqueue = Stopwatch.StartNew();
        await Task.Run(() =>
        {
            sink.Found(RowCount);
            // Deliberately opposite to the final ordering: completion must really sort.
            foreach (var row in rows.Reverse()) sink.File(row);
        });
        enqueue.Stop();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var window = new MainWindow { Width = 1180, Height = 760 };
            window.Show();
            window.Show("processing");
            var page = window.Processing;
            page.BeginSession(session);
            try
            {
                var table = page.FindControl<ListBox>("Table")!;
                var search = page.FindControl<TextBox>("SearchBox")!;
                var statuses = page.FindControl<ComboBox>("StatusBox")!;
                var source = Assert.IsAssignableFrom<INotifyCollectionChanged>(table.ItemsSource);
                var pumpTimes = new List<double>();
                var heartbeatTimes = new List<double>();
                var largestBatch = 0;
                var additions = 0;
                var resets = 0;
                var notifications = 0;
                var activeContainers = 0;
                var selectionEstablished = false;
                void Changed(object? sender, NotifyCollectionChangedEventArgs e)
                {
                    notifications++;
                    if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
                    if (e.Action == NotifyCollectionChangedAction.Add)
                    {
                        Assert.InRange(e.NewItems!.Count, 1, BatchLimit);
                        additions += e.NewItems.Count;
                    }
                }
                source.CollectionChanged += Changed;
                Render(window);
                var ingestion = Stopwatch.StartNew();
                while (session.HasPendingRows)
                {
                    var timer = Stopwatch.StartNew();
                    var beforePumpNotifications = notifications;
                    var count = page.Pump();
                    timer.Stop();
                    Assert.InRange(count, 1, BatchLimit);
                    Assert.Equal(1, notifications - beforePumpNotifications);
                    largestBatch = Math.Max(largestBatch, count);
                    pumpTimes.Add(timer.Elapsed.TotalMilliseconds);
                    if (!selectionEstablished)
                    {
                        table.SelectedItem = rows[^1];
                        selectionEstablished = true;
                    }
                    Assert.Same(rows[^1], table.SelectedItem);
                    // Input gets an opportunity after every bounded update, including while rows arrive.
                    heartbeatTimes.Add(await Heartbeat());
                    Assert.Same(rows[^1], table.SelectedItem);
                    if (pumpTimes.Count % 10 == 1 || !session.HasPendingRows)
                    {
                        Render(window);
                        activeContainers = Math.Max(activeContainers, Realized(table));
                        Assert.InRange(Realized(table), 1, 199);
                        Assert.Contains(table.GetVisualDescendants(), c => c is VirtualizingStackPanel);
                    }
                    Assert.True(pumpTimes.Count <= RowCount, "Очередь результатов перестала продвигаться");
                }
                ingestion.Stop();
                Assert.Equal(RowCount, table.ItemCount);
                Assert.Equal(RowCount, additions);
                Assert.Equal(0, resets);
                Assert.InRange(notifications, 1, RowCount);
                Assert.Same(source, table.ItemsSource);
                var unchangedNotifications = notifications;
                Assert.Equal(0, page.Pump());
                Assert.Equal(unchangedNotifications, notifications);
                source.CollectionChanged -= Changed;

                var finish = Stopwatch.StartNew();
                await page.CompleteSessionAsync();
                finish.Stop();
                Render(window);
                Assert.False(page.IsRunning);
                Assert.Equal(rows, table.Items.Cast<ResultRow>());
                Assert.Same(rows[^1], table.SelectedItem);

                var scroll = Stopwatch.StartNew();
                table.ScrollIntoView(rows[^1]);
                Render(window);
                scroll.Stop();
                var endContainers = Realized(table);
                Assert.InRange(endContainers, 1, 199);
                Assert.Contains(table.GetVisualDescendants().OfType<ListBoxItem>(), c => ReferenceEquals(c.DataContext, rows[^1]));
                Assert.True(page.FindControl<Border>("FileCard")!.IsVisible);
                Assert.Equal(rows[^1].Was, page.FindControl<TextBlock>("CardTitle")!.Text);
                Assert.Equal(rows[^1].StatusText, page.FindControl<TextBlock>("CardStatus")!.Text);
                Assert.Equal(rows[^1].Reason, page.FindControl<TextBlock>("CardReason")!.Text);

                var searchOld = Stopwatch.StartNew();
                search.Text = "scan_19999";
                Render(window);
                searchOld.Stop();
                Assert.Same(rows[^1], Assert.Single(table.Items.Cast<ResultRow>()));
                Assert.Same(rows[^1], table.SelectedItem);
                search.Text = "";
                Render(window);

                var statusTimes = new List<double>();
                for (var filter = 1; filter <= 4; filter++)
                {
                    var timer = Stopwatch.StartNew();
                    statuses.SelectedIndex = filter;
                    Render(window);
                    timer.Stop();
                    statusTimes.Add(timer.Elapsed.TotalMilliseconds);
                    Assert.Equal(RowCount / 4, table.ItemCount);
                    Assert.All(table.Items.Cast<ResultRow>(), row => Assert.Equal((StatusKind)(filter - 1), row.Kind));
                    Assert.NotNull(table.SelectedItem);
                    Assert.Contains(table.SelectedItem, table.Items);
                }
                statuses.SelectedIndex = 1;
                var searchNew = Stopwatch.StartNew();
                search.Text = "Деталь 19996";
                Render(window);
                searchNew.Stop();
                Assert.Same(rows[19996], Assert.Single(table.Items.Cast<ResultRow>()));
                Assert.Same(rows[19996], table.SelectedItem);
                Assert.Equal(rows[19996].NewName, page.FindControl<TextBlock>("CardName")!.Text);

                search.Text = "";
                statuses.SelectedIndex = 0;
                table.SelectedItem = rows[^1];
                table.ScrollIntoView(rows[^1]);
                Render(window);
                SaveScreenshot(window);

                // A generous regression ceiling, not a speed claim: a single UI update must not
                // monopolize the thread for seconds even on shared CI. Actual values are recorded.
                Assert.True(pumpTimes.Max() < 2000, $"Один пакет заблокировал окно на {pumpTimes.Max():F0} мс");
                Assert.True(heartbeatTimes.Max() < 2000, $"Окно не отвечало на ввод {heartbeatTimes.Max():F0} мс");
                Record(new
                {
                    Test = nameof(Twenty_thousand_results_keep_virtualized_rows_selection_search_and_dispatcher_working),
                    VerifiedUtc = DateTimeOffset.UtcNow,
                    Runtime = RuntimeInformation.FrameworkDescription,
                    Platform = RuntimeInformation.OSDescription,
                    Rows = RowCount,
                    OcrInvoked = false,
                    WindowWidth = window.Width,
                    WindowHeight = window.Height,
                    EnqueueMilliseconds = enqueue.Elapsed.TotalMilliseconds,
                    IngestionMilliseconds = ingestion.Elapsed.TotalMilliseconds,
                    PumpCount = pumpTimes.Count,
                    MaximumBatch = largestBatch,
                    PumpMilliseconds = new { Maximum = pumpTimes.Max(), P95 = Percentile(pumpTimes, .95), Total = pumpTimes.Sum() },
                    DispatcherHeartbeatMilliseconds = new { Maximum = heartbeatTimes.Max(), P95 = Percentile(heartbeatTimes, .95) },
                    IncrementalCollectionNotifications = notifications,
                    CollectionResetsDuringIngestion = resets,
                    MaximumRealizedRowsDuringIngestion = activeContainers,
                    RealizedRowsAtEnd = endContainers,
                    CompletionAndSortMilliseconds = finish.Elapsed.TotalMilliseconds,
                    ScrollToLastMilliseconds = scroll.Elapsed.TotalMilliseconds,
                    SearchOldNameMilliseconds = searchOld.Elapsed.TotalMilliseconds,
                    SearchNewNameMilliseconds = searchNew.Elapsed.TotalMilliseconds,
                    StatusFilterMilliseconds = statusTimes,
                    Result = "passed",
                });
                return 0;
            }
            finally
            {
                if (page.IsRunning) await page.CompleteSessionAsync();
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile) =>
        values.Order().ElementAt(Math.Clamp((int)Math.Ceiling(values.Count * percentile) - 1, 0, values.Count - 1));

    private static void Record(object result)
    {
        var output = Environment.GetEnvironmentVariable("DTR_UI_PERFORMANCE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "results-20000.json"), JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
    }

    private static void SaveScreenshot(Window window)
    {
        var output = Environment.GetEnvironmentVariable("DTR_SCREENS");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(output, "results-20000-last-row.png"));
    }
}
