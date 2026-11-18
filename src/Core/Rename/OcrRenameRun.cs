using System.Threading.Channels;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Templates;

namespace DocumentTextRecognition.Core.Rename;

/// <summary>Чем закончилась обработка файла.</summary>
internal enum FileStatus
{
    Renamed,
    /// <summary>Имя уже соответствует результату — переименовывать нечего.</summary>
    AlreadyNamed,
    Skipped,
    /// <summary>Сбой при обработке (не причина в данных, а ошибка) — тоже пропуск.</summary>
    Error,
}

/// <summary>Строка результата: файл (путь от исходной папки), итог, новое имя, причина, шаблон
/// и полный путь к файлу сейчас (после переименования — новый).</summary>
internal sealed record JournalEntry(string RelativePath, FileStatus Status, string? NewName, string? Reason, string? Template,
    string? FullPath = null)
{
    public RecognitionProvenance? Trace { get; init; }

    // Trace is measurement metadata, not part of the established journal value identity.
    public bool Equals(JournalEntry? other) => ReferenceEquals(this, other) || other is not null &&
        RelativePath == other.RelativePath && Status == other.Status && NewName == other.NewName &&
        Reason == other.Reason && Template == other.Template && FullPath == other.FullPath;

    public override int GetHashCode() => HashCode.Combine(RelativePath, Status, NewName, Reason, Template, FullPath);
}

/// <summary>Что делать, если новое имя уже занято другим файлом.</summary>
internal enum NameTakenPolicy
{
    /// <summary>Пропустить файл с причиной.</summary>
    Skip,
    /// <summary>Добавить номер: «… (2).pdf», «… (3).pdf».</summary>
    AddNumber,
}

/// <summary>Параметры запуска.</summary>
internal sealed record RunSettings(string TemplatesFolder, string SourceFolder, bool Recursive, int Workers = 0,
    NameTakenPolicy NameTaken = NameTakenPolicy.Skip)
{
    /// <summary>Наибольший номер при занятом имени.</summary>
    public const int MaxNumber = 99;

    /// <summary>Автоматический параллелизм по процессорам и доступному ОС/контейнеру объему
    /// памяти. У каждого обработчика свои модели и нативные буферы; их память не управляется GC.</summary>
    public static int DefaultWorkers => RecommendWorkers(Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    internal static int RecommendWorkers(int processorCount, long totalAvailableMemoryBytes)
    {
        var processors = Math.Max(1, processorCount);
        // Если лимит памяти неизвестен, сохраняем консервативный предел четырех моделей.
        if (totalAvailableMemoryBytes <= 0) return Math.Min(processors, 4);

        // Один/два процессора не перегружаем. От трех процессоров небольшое перекрытие
        // рендеринга, OCR и файловых операций быстрее в замере; максимум — восемь файлов.
        var cpuBudget = processors <= 2 ? processors : Math.Min(processors, 4) * 2;
        const long gibibyte = 1024L * 1024 * 1024;
        // Пороги оставляют запас на ОС и приложение: примерно ПК с 16/8/4 ГБ памяти.
        var memoryBudget = totalAvailableMemoryBytes >= 12 * gibibyte ? 8
            : totalAvailableMemoryBytes >= 6 * gibibyte ? 4
            : totalAvailableMemoryBytes >= 3 * gibibyte ? 2
            : 1;
        return Math.Min(cpuBudget, memoryBudget);
    }

    /// <summary>Отдельные процессы не разделяют блокировку PDFium, поэтому автоматический
    /// режим не создает больше процессов, чем CPU. Память ограничивается теми же резервами.</summary>
    internal static int DefaultProcessWorkers => RecommendProcessWorkers(Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    internal static int RecommendProcessWorkers(int processorCount, long totalAvailableMemoryBytes) =>
        Math.Min(Math.Clamp(processorCount, 1, 8), RecommendWorkers(processorCount, totalAvailableMemoryBytes));

    public int EffectiveWorkers => Workers > 0 ? Workers : UseProcessWorkers ? DefaultProcessWorkers : DefaultWorkers;

    /// <summary>Один постоянный поток на распознаватель: нативные кэши oneDNN привязаны к потоку.</summary>
    internal bool DedicatedWorkers { get; init; } = true;

    /// <summary>Экспериментальный пул независимых OCR-процессов. Переименовывает только родитель.</summary>
    internal bool UseProcessWorkers { get; init; }

    /// <summary>Явная команда запуска для интеграционной проверки; обычно используется зарегистрированный host.</summary>
    internal OcrWorkerLaunchCommand? WorkerHostOverride { get; init; }
}

/// <summary>Куда прогон сообщает о ходе работы. Вызывается из фоновых потоков.</summary>
internal interface IRunSink
{
    /// <summary>Сообщение о ходе (шаблоны, найдено файлов, итог) в журнал.</summary>
    void Message(string text);

    /// <summary>Файлов найдено — до начала обработки.</summary>
    void Found(int count);

    /// <summary>Файл обработан.</summary>
    void File(JournalEntry entry);
}

/// <summary>Распознавание одного документа; экземпляр — на рабочий поток.</summary>
internal interface IDocumentRecognizer : IDisposable
{
    DocumentResult Read(string path, CancellationToken ct);
}

/// <summary>Настоящее распознавание: шаблоны, совмещение, Tesseract и PaddleOCR.</summary>
internal sealed class OcrRecognizer(IReadOnlyList<DocTemplate> templates, OcrPair pair) : IDocumentRecognizer
{
    public DocumentResult Read(string path, CancellationToken ct) => DocumentReader.Read(path, templates, pair, ct);
    public void Dispose() => pair.Dispose();
}

/// <summary>Итог прогона.</summary>
internal sealed record RunSummary(int Found, int Processed, int Renamed, int AlreadyNamed, int Skipped, bool Stopped, string? Fatal)
{
    public int NotProcessed => Found - Processed;
}

/// <summary>
/// Прогон «по содержимому»: шаблоны → список файлов → распознавание в несколько потоков →
/// переименование по одному.
///
/// <para>Распознавание идет параллельно (<see cref="RunSettings.EffectiveWorkers"/> потоков, у
/// каждого своя пара движков), очередь между шагами ограничена — в памяти одновременно лишь
/// несколько документов. Переименование — в одном потоке: конфликт имен проверяется прямо перед
/// переименованием, и два документа с одним итоговым именем не могут «проскочить» вместе.</para>
/// <para>Остановка: новые файлы не берутся; работающие документы не переименовываются,
/// дочерние OCR-процессы завершаются; уже переименованные остаются. Переименование — одна атомарная операция
/// файловой системы, недоделанных файлов не бывает. Содержимое PDF не меняется никогда.</para>
/// </summary>
internal static class OcrRenameRun
{
    public static RunSummary Execute(RunSettings settings, Func<OcrPair> engines, IRunSink sink, CancellationToken ct)
    {
        if (!Directory.Exists(settings.TemplatesFolder))
            return Fatal(sink, "Папка шаблонов не найдена.");
        if (!Directory.Exists(settings.SourceFolder))
            return Fatal(sink, "Папка с PDF не найдена.");

        TemplateSnapshot? captured = null;
        try
        {
            if (settings.UseProcessWorkers) captured = TemplateSnapshot.Create(settings.TemplatesFolder, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new RunSummary(0, 0, 0, 0, 0, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fatal(sink, $"Не удалось подготовить общий набор шаблонов: {ex.Message}");
        }
        using var snapshot = captured;
        var templateFolder = snapshot?.Folder ?? settings.TemplatesFolder;
        // В процессном режиме родитель и дети загружают один и тот же неизменяемый набор.
        var loads = TemplateLibrary.LoadFolder(templateFolder, ct);
        var templates = loads.Where(l => l.Template is not null).Select(l => l.Template!).ToList();
        try
        {
            foreach (var load in loads)
                sink.Message(load.Template is { } t
                    ? $"Шаблон «{t.Name}»: {Describe(t)}"
                    : $"Шаблон {load.FileName} не используется: {load.Problem}");
            if (loads.Count == 0) return Fatal(sink, "В папке шаблонов нет PDF-файлов.");
            if (templates.Count == 0) return Fatal(sink, "Нет ни одного пригодного шаблона: исправьте разметку.");

            var files = ListSource(settings, snapshot?.SourceFiles ?? loads.Select(l => l.FilePath));
            sink.Found(files.Count);
            if (files.Count == 0)
            {
                sink.Message("PDF-файлов для обработки не найдено.");
                return new RunSummary(0, 0, 0, 0, 0, false, null);
            }
            if (!settings.UseProcessWorkers)
                return ProcessFiles(settings, files, () => new OcrRecognizer(templates, engines()), sink, ct);

            OcrWorkerConfiguration configuration;
            try
            {
                ct.ThrowIfCancellationRequested();
                // Фабрика вызывается один раз: передается ее фактическая конфигурация, а не
                // неявные настройки по умолчанию. Нативные модели probe освобождаются до детей.
                using var probe = engines();
                configuration = probe.ExportWorkerConfiguration(templateFolder);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new RunSummary(files.Count, 0, 0, 0, 0, true, null);
            }
            catch (Exception ex)
            {
                return Fatal(sink, $"OCR не запускается: {Ocr.OcrComponents.Explain(ex)}");
            }
            return ProcessFiles(settings, files,
                () => new OcrWorkerClient(configuration, ct, settings.WorkerHostOverride), sink, ct);
        }
        finally
        {
            foreach (var t in templates) t.Dispose();
        }
    }

    private static string Describe(DocTemplate t)
    {
        var size = $"{t.PageSize.WidthMm:F0}×{t.PageSize.HeightMm:F0} мм";
        var fields = t.HasCombined ? "общая область" : "обозначение, наименование и тип документа";
        return $"{fields}, лист {size}" + (t.PageIndex > 0 ? $", стр. {t.PageIndex + 1}" : "");
    }

    private static RunSummary Fatal(IRunSink sink, string message)
    {
        sink.Message(message);
        return new RunSummary(0, 0, 0, 0, 0, false, message);
    }

    /// <summary>
    /// Файлы для обработки: *.pdf в папке (и подпапках), кроме папки шаблонов и самих
    /// файлов-шаблонов — даже если шаблоны лежат внутри обрабатываемого дерева. Недоступные
    /// папки пропускаются.
    /// </summary>
    public static List<string> ListSource(RunSettings settings, IEnumerable<string> templateFiles)
    {
        var comparison = FileNames.PathComparison;
        var templatesDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.TemplatesFolder)) + Path.DirectorySeparatorChar;
        var excluded = templateFiles.Select(Path.GetFullPath).ToHashSet(
            comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = settings.Recursive,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
            AttributesToSkip = FileAttributes.System,
        };
        return Directory.EnumerateFiles(settings.SourceFolder, "*", options)
            .Where(f => f.EndsWith(FileNames.Extension, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .Where(f => !f.StartsWith(templatesDir, comparison) && !excluded.Contains(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    private sealed record Planned(string Path, string Relative, long Length, DateTime Written,
        string? NewName, string? Problem, FileStatus? Status, string? Template)
    {
        internal RecognitionProvenance? Trace { get; init; }
    }

    /// <summary>Обработать готовый список файлов: распознавание в
    /// <see cref="RunSettings.EffectiveWorkers"/> потоках (у каждого свой распознаватель),
    /// переименование — по одному в вызывающем потоке.</summary>
    internal static RunSummary ProcessFiles(RunSettings settings, IReadOnlyList<string> files,
        Func<IDocumentRecognizer> recognizers, IRunSink sink, CancellationToken ct,
        Func<Action, Task>? launchDedicatedWorker = null)
    {
        // Движки создаются заранее: не поднялся хоть один — честно сообщаем, а не работаем
        // одним движком или меньшим числом потоков.
        var pairs = new List<IDocumentRecognizer>();
        try
        {
            for (var i = 0; i < Math.Min(settings.EffectiveWorkers, files.Count); i++)
            {
                ct.ThrowIfCancellationRequested();
                pairs.Add(recognizers());
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var cleanup = DisposeAll(pairs);
            var message = cleanup is null ? null : $"Ошибка остановки OCR: {Ocr.OcrComponents.Explain(cleanup)}";
            if (message is not null) sink.Message(message);
            return new RunSummary(files.Count, 0, 0, 0, 0, true, message);
        }
        catch (Exception ex)
        {
            var cleanup = DisposeAll(pairs);
            return Fatal(sink, $"OCR не запускается: {Ocr.OcrComponents.Explain(WithCleanup(ex, cleanup))}");
        }

        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(settings.EffectiveWorkers * 2)
        {
            SingleWriter = true,
        });
        var planned = Channel.CreateBounded<Planned>(new BoundedChannelOptions(64) { SingleReader = true });
        int processed = 0, renamed = 0, already = 0, skipped = 0;

        using var pipelineStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workToken = pipelineStop.Token;
        var workers = new List<Task>(pairs.Count);
        var producer = Task.CompletedTask;
        try
        {
            producer = Task.Run(async () =>
            {
                try
                {
                    foreach (var f in files)
                    {
                        if (workToken.IsCancellationRequested) break;
                        await queue.Writer.WriteAsync(f, workToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                finally { queue.Writer.TryComplete(); }
            });

            foreach (var pair in pairs)
            {
                Task worker;
                if (settings.DedicatedWorkers)
                {
                    Action dedicatedWork = () =>
                    {
                        using (pair)
                        {
                            try
                            {
                                foreach (var path in queue.Reader.ReadAllAsync(workToken).ToBlockingEnumerable())
                                {
                                    var item = Recognize(path, settings.SourceFolder, pair, workToken);
                                    if (item is null) break;
                                    var write = planned.Writer.WriteAsync(item, workToken);
                                    if (write.IsCompletedSuccessfully) write.GetAwaiter().GetResult();
                                    else write.AsTask().GetAwaiter().GetResult();
                                }
                            }
                            catch (OperationCanceledException) { }
                        }
                    };
                    worker = launchDedicatedWorker is not null ? launchDedicatedWorker(dedicatedWork)
                        : Task.Factory.StartNew(dedicatedWork, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                else
                {
                    worker = Task.Run(async () =>
                    {
                        using (pair)
                        {
                            try
                            {
                                await foreach (var path in queue.Reader.ReadAllAsync(workToken).ConfigureAwait(false))
                                {
                                    var item = Recognize(path, settings.SourceFolder, pair, workToken);
                                    if (item is null) break;
                                    await planned.Writer.WriteAsync(item, workToken).ConfigureAwait(false);
                                }
                            }
                            catch (OperationCanceledException) { }
                        }
                    });
                }
                workers.Add(worker);
            }
        }
        catch (Exception ex)
        {
            // Если ОС не создала очередной поток, остановить и дождаться уже запущенных
            // работников до освобождения шаблонов. Неназначенные движки тоже освобождаются.
            pipelineStop.Cancel();
            try { Task.WhenAll(workers).GetAwaiter().GetResult(); } catch (Exception) { }
            try { producer.GetAwaiter().GetResult(); } catch (Exception) { }
            var cleanup = DisposeAll(pairs.Skip(workers.Count));
            return Fatal(sink, $"Не удалось запустить обработчики OCR: {Ocr.OcrComponents.Explain(WithCleanup(ex, cleanup))}");
        }

        var finishedWorkers = Task.WhenAll(workers);
        var all = finishedWorkers.ContinueWith(completed =>
        {
            if (completed.IsFaulted) pipelineStop.Cancel();
            planned.Writer.TryComplete();
        }, TaskScheduler.Default);

        // Переименование — в этом потоке, по одному.
        try
        {
            foreach (var item in planned.Reader.ReadAllAsync(CancellationToken.None).ToBlockingEnumerable())
            {
                if (ct.IsCancellationRequested) continue; // в работе, но не переименовывается
                var entry = Apply(item, settings.NameTaken);
                processed++;
                switch (entry.Status)
                {
                    case FileStatus.Renamed: renamed++; break;
                    case FileStatus.AlreadyNamed: already++; break;
                    default: skipped++; break;
                }
                sink.File(entry);
            }
        }
        finally
        {
            // Если получатель результатов выбросил исключение, писатели ограниченной
            // очереди тоже должны выйти, а дочерние процессы — завершиться.
            pipelineStop.Cancel();
            all.Wait(CancellationToken.None);
            producer.Wait(CancellationToken.None);
        }

        if (finishedWorkers.Exception is { } failure)
        {
            var message = $"Ошибка завершения OCR: {Ocr.OcrComponents.Explain(failure)}";
            sink.Message(message);
            return new RunSummary(files.Count, processed, renamed, already, skipped, ct.IsCancellationRequested, message);
        }
        return new RunSummary(files.Count, processed, renamed, already, skipped, ct.IsCancellationRequested, null);
    }

    private static Exception? DisposeAll(IEnumerable<IDocumentRecognizer> recognizers)
    {
        List<Exception>? errors = null;
        foreach (var recognizer in recognizers)
        {
            try { recognizer.Dispose(); }
            catch (Exception ex) { (errors ??= []).Add(ex); }
        }
        return errors is null ? null : new AggregateException(errors);
    }

    private static Exception WithCleanup(Exception primary, Exception? cleanup) =>
        cleanup is null ? primary : new AggregateException(primary, cleanup);

    /// <summary>Распознать файл и решить, как его назвать. null — остановлено посреди файла.</summary>
    private static Planned? Recognize(string path, string root, IDocumentRecognizer recognizer, CancellationToken ct)
    {
        var relative = Path.GetRelativePath(root, path);
        long length = 0;
        var written = DateTime.MinValue;
        try
        {
            var info = new FileInfo(path);
            length = info.Length;
            written = info.LastWriteTimeUtc;

            var result = recognizer.Read(path, ct);
            if (result.Values is null)
                return new Planned(path, relative, length, written, null, result.Problem, FileStatus.Skipped, result.Template)
                    { Trace = result.Trace };

            var (name, problem) = FileNames.Build(result.Values);
            return name is null
                ? new Planned(path, relative, length, written, null, problem, FileStatus.Skipped, result.Template)
                    { Trace = result.Trace }
                : new Planned(path, relative, length, written, name, null, null, result.Template)
                    { Trace = result.Trace };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Planned(path, relative, length, written, null, $"файл недоступен: {ex.Message}", FileStatus.Skipped, null);
        }
        catch (Exception ex)
        {
            return new Planned(path, relative, length, written, null, $"ошибка обработки: {ex.Message}", FileStatus.Error, null);
        }
    }

    /// <summary>Переименовать с проверками прямо перед операцией.</summary>
    private static JournalEntry Apply(Planned p, NameTakenPolicy policy)
    {
        if (p.NewName is null)
            return new JournalEntry(p.Relative, p.Status ?? FileStatus.Skipped, null, p.Problem, p.Template, p.Path)
                { Trace = p.Trace };

        var current = Path.GetFileName(p.Path);
        if (IsSameName(current, p.NewName, policy))
            return new JournalEntry(p.Relative, FileStatus.AlreadyNamed, current, "Имя уже соответствует результату", p.Template, p.Path)
                { Trace = p.Trace };

        var info = new FileInfo(p.Path);
        if (!info.Exists) return Skip(p, "файл исчез до переименования");
        if (info.Length != p.Length || info.LastWriteTimeUtc != p.Written) return Skip(p, "файл изменился во время обработки");

        var (status, newName, reason, fullPath) = Renamer.Move(p.Path, p.NewName, policy);
        return status == FileStatus.Renamed
            ? new JournalEntry(p.Relative, FileStatus.Renamed, newName, null, p.Template, fullPath) { Trace = p.Trace }
            : new JournalEntry(p.Relative, FileStatus.Skipped, null, reason, p.Template, p.Path) { Trace = p.Trace };
    }

    private static JournalEntry Skip(Planned p, string reason) =>
        new(p.Relative, FileStatus.Skipped, null, reason, p.Template, p.Path) { Trace = p.Trace };

    /// <summary>Файл уже назван так, как нужно; при правиле «добавить номер» подходит и имя с
    /// номером, полученное прошлым запуском.</summary>
    internal static bool IsSameName(string current, string newName, NameTakenPolicy policy)
    {
        if (string.Equals(current, newName, StringComparison.Ordinal)) return true;
        if (policy != NameTakenPolicy.AddNumber) return false;
        var stem = Path.GetFileNameWithoutExtension(newName);
        return System.Text.RegularExpressions.Regex.IsMatch(current,
            "^" + System.Text.RegularExpressions.Regex.Escape(stem) + @" \(([2-9]|[1-9][0-9])\)\.pdf$");
    }
}
