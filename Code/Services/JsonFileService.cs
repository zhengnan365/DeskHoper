using System.Text.Json;
using System.IO;

namespace MonitorSwitch.Services;

public abstract class JsonFileService<T>(string filePath)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    protected string FilePath { get; } = filePath;
    public async Task<T> LoadAsync(T fallback, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(FilePath)) return fallback;
            await using var stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken) ?? fallback;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".corrupt", true); } catch { }
            await AppLogger.ErrorAsync($"Configuration load failed: {FilePath}", ex);
            return fallback;
        }
    }
    public async Task SaveAsync(T value, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);
        File.Move(temp, FilePath, true);
    }
}
