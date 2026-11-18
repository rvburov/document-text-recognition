using DocumentTextRecognition.Core.Templates;

namespace DocumentTextRecognition.Core.Rename;

/// <summary>Один набор байтов шаблонов для родителя и всех дочерних процессов прогона.
/// PDF и разметка копируются без преобразований; исходная папка остается пользовательской.</summary>
internal sealed class TemplateSnapshot : IDisposable
{
    private readonly DirectoryInfo _directory;
    internal string Folder => _directory.FullName;
    internal IReadOnlyList<string> SourceFiles { get; }

    private TemplateSnapshot(DirectoryInfo directory, IReadOnlyList<string> sourceFiles)
    {
        _directory = directory;
        SourceFiles = sourceFiles;
    }

    internal static TemplateSnapshot Create(string sourceFolder, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var directory = Directory.CreateTempSubdirectory("dtr-template-snapshot-");
        var snapshot = new TemplateSnapshot(directory, []);
        try
        {
            var sources = TemplateLibrary.ListFiles(sourceFolder);
            foreach (var source in sources)
            {
                ct.ThrowIfCancellationRequested();
                File.Copy(source, Path.Combine(directory.FullName, Path.GetFileName(source)));
                ct.ThrowIfCancellationRequested();
                var markup = TemplateMarkupStore.PathFor(source);
                if (File.Exists(markup)) File.Copy(markup, Path.Combine(directory.FullName, Path.GetFileName(markup)));
            }
            ct.ThrowIfCancellationRequested();
            return new TemplateSnapshot(directory, sources);
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            // File.Copy can preserve the original Windows read-only attribute.
            foreach (var file in Directory.EnumerateFiles(Folder))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException) { /* Antivirus or an external handle may delay temporary-file cleanup. */ }
        catch (UnauthorizedAccessException) { }
    }
}
