#if !RELEASELIBRARY

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SqlInliner.Optimize;

/// <summary>
/// Manages the session directory for an optimize session: file writes, hashing, and logging.
/// </summary>
public sealed class SessionDirectory
{
    private readonly StreamWriter? logWriter;

    public SessionDirectory(string viewName, string baseDirectory)
    {
        var safeName = viewName.Replace(".", "-").Replace("[", "").Replace("]", "");
        var timestamp = DateTime.Now.ToString("yyyyMMdd'T'HHmmss");
        DirectoryPath = Path.Combine(baseDirectory, $"optimize-{safeName}-{timestamp}");
        Directory.CreateDirectory(DirectoryPath);

        var logPath = Path.Combine(DirectoryPath, "session.log");
        logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
        Log($"Session started for view {viewName}");
    }

    /// <summary>
    /// Gets the full path to the session directory.
    /// </summary>
    public string DirectoryPath { get; }

    /// <summary>
    /// Saves a SQL file for the specified iteration. Returns the full file path.
    /// </summary>
    public string SaveIteration(int iteration, string sql)
    {
        var fileName = $"iteration-{iteration}.sql";
        var path = Path.Combine(DirectoryPath, fileName);
        File.WriteAllText(path, sql);
        Log($"Saved {fileName} ({sql.Length} chars)");
        return path;
    }

    /// <summary>
    /// Saves the recommended SQL file. Returns the full file path.
    /// </summary>
    public string SaveRecommended(string sql)
    {
        var path = Path.Combine(DirectoryPath, "recommended.sql");
        File.WriteAllText(path, sql);
        Log($"Saved recommended.sql ({sql.Length} chars)");
        return path;
    }

    /// <summary>
    /// Computes a SHA256 hash of the file at the specified path.
    /// </summary>
    public static string ComputeHash(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Appends a log entry with timestamp.
    /// </summary>
    public void Log(string message)
    {
        logWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    /// <summary>
    /// Closes the log writer.
    /// </summary>
    public void Close()
    {
        Log("Session ended");
        logWriter?.Dispose();
    }
}

#endif
