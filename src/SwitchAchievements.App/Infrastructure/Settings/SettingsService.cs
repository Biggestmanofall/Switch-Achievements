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

    /// <summary>
    /// The active settings. Replaced by <see cref="Save"/>, so readers that consult it
    /// when they act (for example the main window's close handling) see saved changes
    /// immediately, without a restart.
    /// </summary>
    public AppSettings Current { get; private set; }

    /// <summary>
    /// Makes <paramref name="settings"/> the active settings and persists them atomically.
    /// The active settings change even if writing the file fails (the failure is logged),
    /// so the user's choice applies for this session.
    /// </summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // A private copy: later mutation of the caller's instance cannot change the active settings.
        Current = new AppSettings
        {
            ShowWindowOnLaunch = settings.ShowWindowOnLaunch,
            MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose,
            StartWithWindows = settings.StartWithWindows,
        };

        try
        {
            Directory.CreateDirectory(_directory);
            var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });

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
