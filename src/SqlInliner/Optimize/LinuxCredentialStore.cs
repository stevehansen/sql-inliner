#if !RELEASELIBRARY

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SqlInliner.Optimize;

/// <summary>
/// Linux credential store using secret-tool (libsecret) CLI.
/// Uses a JSON index file for username lookup and listing.
/// </summary>
internal sealed class LinuxCredentialStore : ICredentialStore
{
    private const string ServiceAttribute = "sqlinliner";
    private static readonly string IndexPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".sqlinliner", "credentials.json");

    public LinuxCredentialStore()
    {
        var (exitCode, _) = RunProcess("which", "secret-tool");
        if (exitCode != 0)
            throw new InvalidOperationException(
                "secret-tool is not installed. Install it with:\n" +
                "  Ubuntu/Debian: sudo apt install libsecret-tools\n" +
                "  Fedora/RHEL:   sudo dnf install libsecret\n" +
                "  Arch:          sudo pacman -S libsecret");
    }

    public void Store(string key, string username, string password)
    {
        // secret-tool reads the secret from stdin
        var (exitCode, output) = RunProcess("secret-tool",
            $"store --label=\"sqlinliner: {key}\" service {ServiceAttribute} key {key} username {username}",
            stdinData: password);

        if (exitCode != 0)
            throw new InvalidOperationException($"Failed to store credential: {output}");

        // Update index
        var index = LoadIndex();
        index.RemoveAll(e => e.Key == key);
        index.Add(new IndexEntry { Key = key, Username = username });
        SaveIndex(index);
    }

    public StoredCredential? Retrieve(string key)
    {
        var (exitCode, password) = RunProcess("secret-tool",
            $"lookup service {ServiceAttribute} key {key}");

        if (exitCode != 0)
            return null;

        password = password.TrimEnd('\n', '\r');

        if (string.IsNullOrEmpty(password))
            return null;

        // Get username from index
        var index = LoadIndex();
        var entry = index.Find(e => e.Key == key);
        var username = entry?.Username ?? string.Empty;

        return new StoredCredential(username, password);
    }

    public bool Remove(string key)
    {
        var (exitCode, _) = RunProcess("secret-tool",
            $"clear service {ServiceAttribute} key {key}");

        // Remove from index regardless
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

    private static (int ExitCode, string Output) RunProcess(string fileName, string arguments, string? stdinData = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinData != null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process == null)
            return (-1, "Failed to start process");

        if (stdinData != null)
        {
            process.StandardInput.Write(stdinData);
            process.StandardInput.Close();
        }

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
