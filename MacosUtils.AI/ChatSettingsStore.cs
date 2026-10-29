using System.Text.Json;
using MacosUtils.Core;

namespace MacosUtils.AI;

/// <summary>Persisted chat settings (endpoint, model, reasoning effort). Same JSON shape as WindowsUtils.</summary>
public sealed record ChatSettings(string Endpoint, string Model, string? ReasoningEffort);

/// <summary>Loads/saves chat settings as JSON. Defaults to ~/Library/Application Support/MacosUtils/chat.json.
/// The API key is never in this file; it lives in the Keychain (<see cref="CredentialStore"/>).</summary>
public static class ChatSettingsStore
{
    public static string DefaultPath => Path.Combine(AppPaths.SettingsDirectory, "chat.json");

    public static ChatSettings? Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize<ChatSettings>(File.ReadAllText(path));
    }

    public static void Save(ChatSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        AppPaths.EnsurePrivateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Delete(string? path = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path))
            File.Delete(path);
    }
}
