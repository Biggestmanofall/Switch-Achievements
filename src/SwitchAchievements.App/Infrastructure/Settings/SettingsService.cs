using System.IO;
using System.Text.Json;
using SwitchAchievements.App.Infrastructure.Logging;

namespace SwitchAchievements.App.Infrastructure.Settings;

/// <summary>
/// Loads and saves application settings as JSON in a directory supplied by the
/// caller. A missing file produces defaults (and writes them), and a corrupted
/// file is backed up before falling back to defaults. Writes are atomic so a
/// crash mid-save cannot truncate the config.
/// </summary>
public sealed class SettingsService
{
    private readonly string _directory;
    private readonly string _filePath;
    private readonly IAppLogger _logger;

    public SettingsService(string directory, IAppLogger? logger = null)
    {
        _directory = directory;
        _filePath = Path.Combine(directory, "config.json");
        _logger = logger ?? NullLogger.Instance;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (IOException ex)
        {
            _logger.Warn($"could not save settings: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warn($"could not save settings: {ex.Message}");
        }
    }

    private AppSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            var defaults = new AppSettings();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            return settings ?? new AppSettings();
        }
        catch (JsonException)
        {
            BackUpCorruptedFile();
            return new AppSettings();
        }
        catch (IOException ex)
        {
            _logger.Warn($"could not read settings: {ex.Message}");
            return new AppSettings();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warn($"could not read settings: {ex.Message}");
            return new AppSettings();
        }
    }

    private void BackUpCorruptedFile()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupPath = $"{_filePath}.corrupt-{stamp}";
        try
        {
            File.Move(_filePath, backupPath);
            _logger.Warn($"config.json was corrupted; moved to {backupPath}");
        }
        catch (IOException ex)
        {
            _logger.Warn($"could not back up corrupted config: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warn($"could not back up corrupted config: {ex.Message}");
        }
    }
}
