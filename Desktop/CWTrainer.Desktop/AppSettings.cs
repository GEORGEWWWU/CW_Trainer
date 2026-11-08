using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CWTrainer.Desktop;

/// <summary>界面的明暗模式。</summary>
public enum AppTheme
{
    /// <summary>跟随 Windows 的「应用模式」。</summary>
    System = 0,
    Light = 1,
    Dark = 2
}

/// <summary>
/// 用户偏好，存到 %AppData%\CWTrainer\settings.json。
/// 目前只有外观和电脑音频两项，读写都做了容错，坏了就当默认值用。
/// </summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>CW 音调（Hz）。</summary>
    public int ToneHz { get; set; } = 650;

    /// <summary>电脑音量（%）。</summary>
    public int Volume { get; set; } = 45;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CWTrainer",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            string json = File.ReadAllText(FilePath);
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);

            return loaded ?? new AppSettings();
        }
        catch
        {
            // 配置损坏不该挡住启动
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 写不进去也不影响使用
        }
    }
}
