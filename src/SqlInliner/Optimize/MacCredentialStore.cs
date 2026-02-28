#if !RELEASELIBRARY

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SqlInliner.Optimize;

/// <summary>
/// macOS Keychain implementation using the security CLI.
/// Uses a JSON index file for username lookup and listing.
/// </summary>
internal sealed class MacCredentialStore : ICredentialStore
{
    private const string ServiceName = "sqlinliner";
    private static readonly string IndexPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".sqlinliner", "credentials.json");

    public MacCredentialStore()
    {
        // Verify security CLI is available
        var (exitCode, _) = RunProcess("which", "security");
        if (exitCode != 0)
            throw new InvalidOperationException("macOS 'security' command not found. Cannot access Keychain.");
    }

    public void Store(string key, string username, string password)
    {
        var serviceName = $"{ServiceName}:{key}";

        // -U updates if exists, -a account, -s service, -w password
        var (exitCode, output) = RunProcess("security", $"add-generic-password -a \"{username}\" -s \"{serviceName}\" -w \"{password}\" -U");
        if (exitCode != 0)
            throw new InvalidOperationException($"Failed to store credential in Keychain: {output}");

        // Update index
        var index = LoadIndex();
        index.RemoveAll(e => e.Key == key);
        index.Add(new IndexEntry { Key = key, Username = username });
        SaveIndex(index);
    }

    public StoredCredential? Retrieve(string key)
    {
        var serviceName = $"{ServiceName}:{key}";

        // Get password
        var (exitCode, password) = RunProcess("security", $"find-generic-password -w -s \"{serviceName}\"");
        if (exitCode != 0)
            return null;

        password = password.Trim();

        // Get username from index
        var index = LoadIndex();
        var entry = index.Find(e => e.Key == key);
        var username = entry?.Username ?? string.Empty;

        return new StoredCredential(username, password);
    }

    public bool Remove(string key)
    {
        var serviceName = $"{ServiceName}:{key}";

        var (exitCode, _) = RunProcess("security", $"delete-generic-password -s \"{serviceName}\"");

        // Remove from index regardless of Keychain result
        var index = LoadIndex();
        var removed = index.RemoveAll(e => e.Key == key) > 0;
        SaveIndex(index);

        return exitCode == 0 || removed;
    }

    public IReadOnlyList<(string Key, string Username)> List()
    {
        var result = new List<(string Key, string Username)>();
        var index = LoadIndex();
        foreach (var entry in index)
            result.Add((entry.Key, entry.Username));
        return result;
    }

    private static (int ExitCode, string Output) RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process == null)
            return (-1, "Failed to start process");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, string.IsNullOrEmpty(output) ? error : output);
    }

    private sealed class IndexEntry
    {
        public string Key { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
    }

    private static List<IndexEntry> LoadIndex()
    {
        if (!File.Exists(IndexPath))
            return new List<IndexEntry>();

        try
        {
            var json = File.ReadAllText(IndexPath);
            return JsonSerializer.Deserialize<List<IndexEntry>>(json) ?? new List<IndexEntry>();
        }
        catch
        {
            return new List<IndexEntry>();
        }
    }

    private static void SaveIndex(List<IndexEntry> index)
    {
        var dir = Path.GetDirectoryName(IndexPath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(IndexPath, json);
    }
}

#endif
