using DocumentTextRecognition.Core.Rename;

namespace DocumentTextRecognition.Services;

/// <summary>Строка таблицы результата: файл, что стало, статус и причина.</summary>
public sealed class ResultRow
{
    internal ResultRow(JournalEntry e)
    {
        Was = e.RelativePath;
        NewName = e.NewName;
        Reason = e.Reason;
        Template = e.Template;
        FullPath = e.FullPath;
        Kind = e.Status switch
        {
            FileStatus.Renamed => StatusKind.Renamed,
            FileStatus.AlreadyNamed => StatusKind.AlreadyNamed,
            FileStatus.Error => StatusKind.Error,
            _ => StatusKind.Skipped,
        };
    }

    public ResultRow(string was, string? newName, string? reason, StatusKind kind, string? fullPath, string? template = null)
    {
        Was = was;
        NewName = newName;
        Reason = reason;
        Kind = kind;
        FullPath = fullPath;
        Template = template;
    }

    /// <summary>Было: путь от папки с PDF.</summary>
    public string Was { get; }

    /// <summary>Новое имя (переименован, имя уже верное).</summary>
    public string? NewName { get; private set; }

    public string? Reason { get; private set; }
    public string? Template { get; }

    /// <summary>Где файл сейчас (после переименования новый путь).</summary>
    public string? FullPath { get; private set; }

    public StatusKind Kind { get; private set; }

    public bool IsNamed => Kind is StatusKind.Renamed or StatusKind.AlreadyNamed;
    public bool IsRenamed => Kind == StatusKind.Renamed;
    public bool IsSkipped => Kind == StatusKind.Skipped;
    public bool IsError => Kind == StatusKind.Error;

    /// <summary>Стало: новое имя или причина, почему файл пропущен.</summary>
    public string Became => IsNamed ? NewName ?? "" : Reason ?? "";

    public string StatusText => Kind switch
    {
        StatusKind.Renamed => "Переименован",
        StatusKind.AlreadyNamed => "Имя уже верное",
        StatusKind.Error => "Ошибка",
        _ => "Пропущен",
    };

    /// <summary>Класс метки статуса в оформлении.</summary>
    public string StatusClass => Kind switch
    {
        StatusKind.Renamed => "success",
        StatusKind.Error => "danger",
        StatusKind.Skipped => "warning",
        _ => "",
    };

    /// <summary>Имя введено вручную: строка становится «Переименован».</summary>
    public void ApplyManual(string newName, string fullPath)
    {
        NewName = newName;
        FullPath = fullPath;
        Reason = null;
        Kind = StatusKind.Renamed;
    }

    public bool Matches(string query) =>
        query.Length == 0 ||
        Was.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        (NewName?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);
}

public enum StatusKind { Renamed, AlreadyNamed, Skipped, Error }
