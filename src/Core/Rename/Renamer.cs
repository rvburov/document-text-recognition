namespace DocumentTextRecognition.Core.Rename;

/// <summary>
/// Переименование одного файла на месте с проверками прямо перед операцией: имя свободно,
/// путь не длиннее предела Windows. Занятое имя: пропуск или номер «(2)», «(3)»… по правилу
/// <see cref="NameTakenPolicy"/>. Содержимое файла не меняется; переименование атомарное.
/// </summary>
internal static class Renamer
{
    public static (FileStatus Status, string? NewName, string? Reason, string? FullPath) Move(string path, string newName,
        NameTakenPolicy policy)
    {
        var dir = Path.GetDirectoryName(path)!;
        var candidate = newName;
        for (var number = 1; ; number++)
        {
            if (number > 1)
            {
                if (policy != NameTakenPolicy.AddNumber || number > RunSettings.MaxNumber)
                    return (FileStatus.Skipped, null, $"имя «{newName}» уже занято другим файлом в этой папке", null);
                candidate = FileNames.Numbered(newName, number);
            }
            var target = Path.Combine(dir, candidate);
            var sameFile = string.Equals(target, path, FileNames.PathComparison);
            if (!sameFile && (File.Exists(target) || Directory.Exists(target))) continue;
            if (OperatingSystem.IsWindows() && target.Length >= 260)
                return (FileStatus.Skipped, null, $"полный путь к файлу с новым именем длиннее 259 символов ({target.Length})", null);

            try
            {
                File.Move(path, target, overwrite: false);
                return (FileStatus.Renamed, candidate, null, target);
            }
            catch (UnauthorizedAccessException)
            {
                return (FileStatus.Skipped, null, "нет прав на переименование", null);
            }
            catch (IOException) when (File.Exists(target))
            {
                continue; // имя заняли между проверкой и переименованием
            }
            catch (IOException ex)
            {
                return (FileStatus.Skipped, null, $"не удалось переименовать (файл занят другой программой?): {ex.Message}", null);
            }
        }
    }

    /// <summary>Имя, введенное вручную для пропущенного файла: те же правила, что у распознанного.</summary>
    public static (FileStatus Status, string? NewName, string? Reason, string? FullPath) Manual(string path,
        string designation, string name, string type, NameTakenPolicy policy)
    {
        if (!File.Exists(path)) return (FileStatus.Skipped, null, "файл не найден", null);
        var (fileName, problem) = FileNames.Build(new Recognition.FieldValues(designation, name, type));
        if (fileName is null) return (FileStatus.Skipped, null, problem, null);
        if (OcrRenameRun.IsSameName(Path.GetFileName(path), fileName, policy))
            return (FileStatus.AlreadyNamed, Path.GetFileName(path), "Имя уже соответствует результату", path);
        return Move(path, fileName, policy);
    }
}
