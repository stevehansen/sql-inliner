#if !RELEASELIBRARY

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlInliner;

/// <summary>
/// Represents a sqlinliner.json configuration file that provides connection strings,
/// default inliner options, and view file mappings.
/// </summary>
internal sealed class InlinerConfig
{
    public string? ConnectionString { get; set; }
    public bool? StripUnusedColumns { get; set; }
    public bool? StripUnusedJoins { get; set; }
    public bool? AggressiveJoinStripping { get; set; }
    public bool? FlattenDerivedTables { get; set; }
    public bool? GenerateCreateOrAlter { get; set; }

    /// <summary>
    /// Maps view names (e.g. "dbo.VPeople") to relative .sql file paths.
    /// Paths resolve relative to the config file's directory.
    /// </summary>
    public Dictionary<string, string>? Views { get; set; }

    /// <summary>
    /// The directory containing the config file, used to resolve relative view paths.
    /// </summary>
    [JsonIgnore]
    public string BaseDirectory { get; private set; } = "";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads a config file from the specified path.
    /// </summary>
    public static InlinerConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<InlinerConfig>(json, JsonOptions) ?? new InlinerConfig();
        config.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        return config;
    }

    /// <summary>
    /// Attempts to load a config file. If <paramref name="explicitPath"/> is provided, loads from that path.
    /// Otherwise, looks for sqlinliner.json in the current directory. Returns null if no config is found.
    /// </summary>
    public static InlinerConfig? TryLoad(string? explicitPath)
    {
        if (explicitPath != null)
            return Load(explicitPath);

        var defaultPath = Path.Combine(Directory.GetCurrentDirectory(), "sqlinliner.json");
        if (File.Exists(defaultPath))
            return Load(defaultPath);

        return null;
    }

    /// <summary>
    /// Reads each view .sql file from <see cref="Views"/> and registers it with the connection.
    /// </summary>
    public void RegisterViews(DatabaseConnection connection)
    {
        if (Views == null)
            return;

        foreach (var (name, relativePath) in Views)
        {
            var fullPath = Path.GetFullPath(Path.Combine(BaseDirectory, relativePath));
            var sql = File.ReadAllText(fullPath);
            var objectName = DatabaseConnection.ParseObjectName(name);
            connection.AddViewDefinition(objectName, sql);
        }
    }
}

#endif
