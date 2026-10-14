using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoClicker.Models;

namespace AutoClicker.Services;

public sealed class AppStore
{
    private readonly string configPath;
    private readonly string dataPath;
    private readonly SemaphoreSlim writes = new(1, 1);
    private string? previousConfig;
    private string? previousData;
    private bool writable = true;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public AppStore(string? configDirectory = null, string? dataDirectory = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        configPath = Path.Combine(configDirectory ?? GetUserDirectory("XDG_CONFIG_HOME", Path.Combine(home, ".config")), "auto-clicker", "settings.json");
        dataPath = Path.Combine(dataDirectory ?? GetUserDirectory("XDG_DATA_HOME", Path.Combine(home, ".local/share")), "auto-clicker", "data.json");
    }

    internal static string GetUserDirectory(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) ? value : fallback;
    }

    private static void ValidateConfiguration(AppConfiguration configuration)
    {
        if (configuration.Version != 1 || configuration.Clicker == null) { throw new InvalidDataException("Unsupported settings format."); }
        configuration.Clicker.Validate();
    }

    private static void ValidateData(AppData data)
    {
        if (data.Version != 1 || data.Macros == null || data.Statistics == null || data.Macros.Count > 1000) { throw new InvalidDataException("Invalid saved application data."); }
        // Validate nested values before totals so damaged JSON can fall back to the backup.
        foreach (var macro in data.Macros) { MacroValidation.Validate(macro); }
        if (data.Macros.Sum(macro => macro.Events.Count) > 200_000 || data.Macros.Select(macro => macro.Id).Distinct().Count() != data.Macros.Count) { throw new InvalidDataException("Invalid saved macro library."); }
        var statistics = data.Statistics;
        if (statistics.Clicks < 0 || statistics.Sessions < 0 || !double.IsFinite(statistics.RunningMs) || statistics.RunningMs < 0 || statistics.RunningMs >= TimeSpan.MaxValue.TotalMilliseconds) { throw new InvalidDataException("Invalid saved usage statistics."); }
    }

    public (AppConfiguration Configuration, AppData Data, string? Warning) Load()
    {
        try
        {
            var config = Read(configPath, new AppConfiguration(1, new()), ValidateConfiguration, out previousConfig, out var recoveredConfig);
            var data = Read(dataPath, new AppData(1, Array.Empty<MacroDefinition>(), new()), ValidateData, out previousData, out var recoveredData);
            return (config, data, recoveredConfig || recoveredData ? "Recovered saved data from its previous valid copy." : null);
        }
        catch (Exception error)
        {
            writable = false;
            return (new(1, new()), new(1, Array.Empty<MacroDefinition>(), new()), "Saved data could not be read. Saving is disabled to preserve it: " + error.Message);
        }
    }

    private static T Read<T>(string path, T defaults, Action<T> validate, out string? previous, out bool recovered)
    {
        recovered = false;
        previous = null;
        if (!File.Exists(path) && !File.Exists(path + ".bak")) { return defaults; }
        Exception? failure = null;
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (new FileInfo(candidate).Length > 32 * 1024 * 1024) { throw new InvalidDataException("Saved file is too large."); }
                var json = File.ReadAllText(candidate);
                var value = JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidDataException("Saved file is empty.");
                validate(value);
                previous = json;
                recovered = candidate != path;
                return value;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) { failure = error; }
        }
        throw new IOException("Neither " + path + " nor its backup is readable.", failure);
    }

    public async Task SaveAsync(AppConfiguration configuration, AppData data)
    {
        if (!writable) { throw new IOException("Saving is disabled because the original data could not be read."); }
        await writes.WaitAsync();
        try
        {
            // Serialize inside the ordered write queue, off the UI thread, so old snapshots cannot overtake newer ones.
            var payload = await Task.Run(() =>
            {
                ValidateConfiguration(configuration);
                ValidateData(data);
                var configJson = JsonSerializer.Serialize(configuration, JsonOptions);
                var dataJson = JsonSerializer.Serialize(data, JsonOptions);
                if (data.Macros.Count > 1000 || data.Macros.Sum(macro => macro.Events.Count) > 200_000 || Encoding.UTF8.GetByteCount(dataJson) > 32 * 1024 * 1024) { throw new IOException("The saved macro library exceeds its storage limit."); }
                return (Config: configJson, Data: dataJson);
            });
            var configJson = payload.Config;
            var dataJson = payload.Data;
            if (configJson != previousConfig) { if (previousConfig != null) { await ReplaceAsync(configPath + ".bak", previousConfig); } await ReplaceAsync(configPath, configJson); previousConfig = configJson; }
            if (dataJson != previousData) { if (previousData != null) { await ReplaceAsync(dataPath + ".bak", previousData); } await ReplaceAsync(dataPath, dataJson); previousData = dataJson; }
        }
        finally { writes.Release(); }
    }

    private static async Task ReplaceAsync(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, contents);
            if (OperatingSystem.IsLinux()) { File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}
