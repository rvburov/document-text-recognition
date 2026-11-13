using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocumentTextRecognition.Common;

/// <summary>
/// Настройки программы в файле settings.json в папке данных пользователя. Последние выбранные
/// папки программа запоминает: при следующем запуске они уже заполнены.
/// </summary>
public sealed class AppSettings
{
    public string TemplatesFolder { get; set; } = "";
    public string SourceFolder { get; set; } = "";
    public bool Recursive { get; set; } = true;

    /// <summary>Сколько файлов распознавать одновременно; 0 значит «Авто» (по числу ядер).</summary>
    public int Workers { get; set; }

    /// <summary>Если имя занято: false значит пропустить файл, true добавить номер «(2)».</summary>
    public bool NumberWhenTaken { get; set; }

    /// <summary>Размер шрифта, проценты: 100, 115, 125, 150.</summary>
    public int FontScale { get; set; } = 100;

    public bool NotifyWhenDone { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;

    /// <summary>Папка обновлений: туда администратор кладет новый установщик.</summary>
    public string UpdateFolder { get; set; } = "";

    public static string FilePath => Path.Combine(AppInfo.DataDir, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Error("Настройки не прочитаны, взяты по умолчанию", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Настройки не сохранены", ex);
        }
    }

    /// <summary>Сбросить к значениям по умолчанию (файл удаляется).</summary>
    public static AppSettings Reset()
    {
        try { File.Delete(FilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return new AppSettings();
    }
}
