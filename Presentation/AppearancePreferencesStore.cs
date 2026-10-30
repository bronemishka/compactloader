using System.IO;
using System.Text.Json;

namespace CompactLoader;

internal static class AppearancePreferencesStore
{
    internal static AppearancePreferences Read(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppearancePreferences>(File.ReadAllText(path)) ?? new();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return new();
    }

    internal static bool Write(string path, AppearancePreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences));
            File.Move(temporary, path, true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
