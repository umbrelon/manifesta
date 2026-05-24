using System.Text.Json;

namespace Manifesta.Core;

/// <summary>
/// Loads and validates <c>manifesta.config.json</c>.
/// </summary>
public static class ConfigLoader
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Load the config from the path specified in <paramref name="options"/>.</summary>
    public static ManifestaConfig Load(GlobalOptions options) => Load(options.Config);

    /// <summary>
    /// Load the config file from the specified path.
    /// </summary>
    /// <exception cref="ManifestaConfigException">
    ///   Thrown if the file is not found or cannot be parsed.
    /// </exception>
    public static ManifestaConfig Load(string configPath)
    {
        var path = Path.GetFullPath(configPath);

        if (!File.Exists(path))
            throw new ManifestaConfigException(
                $"Config file not found: '{path}'. " +
                $"Use --config to specify a custom path.");

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new ManifestaConfigException(
                $"Cannot read config file '{path}': {ex.Message}");
        }

        ManifestaConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ManifestaConfig>(json, _options);
        }
        catch (JsonException ex)
        {
            throw new ManifestaSchemException(
                $"Config file '{path}' contains invalid JSON: {ex.Message}");
        }

        if (config is null)
            throw new ManifestaConfigException(
                $"Config file '{path}' deserialised to null. Is the file empty?");

        return config;
    }
}
