#if !RELEASELIBRARY

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SqlInliner.Optimize;

/// <summary>
/// Represents a stored credential (username + password).
/// </summary>
public sealed class StoredCredential
{
    public string Username { get; }
    public string Password { get; }

    public StoredCredential(string username, string password)
    {
        Username = username;
        Password = password;
    }
}

/// <summary>
/// Abstraction for platform-specific credential storage.
/// </summary>
public interface ICredentialStore
{
    void Store(string key, string username, string password);
    StoredCredential? Retrieve(string key);
    bool Remove(string key);
    IReadOnlyList<(string Key, string Username)> List();
}

/// <summary>
/// Creates the appropriate platform-specific credential store.
/// </summary>
public static class CredentialStoreFactory
{
    /// <summary>
    /// Creates a credential store for the current platform.
    /// Returns null with a warning message if the platform is not supported.
    /// </summary>
    public static ICredentialStore? Create(out string? warning)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            warning = null;
            return new WindowsCredentialStore();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                warning = null;
                return new MacCredentialStore();
            }
            catch (InvalidOperationException ex)
            {
                warning = ex.Message;
                return null;
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                warning = null;
                return new LinuxCredentialStore();
            }
            catch (InvalidOperationException ex)
            {
                warning = ex.Message;
                return null;
            }
        }

        warning = "Credential store is not supported on this platform.";
        return null;
    }

    /// <summary>
    /// Builds a normalized key from server and database names.
    /// Format: "server\database" (lowercase, trimmed).
    /// </summary>
    public static string BuildKey(string server, string database)
    {
        return $"{server.Trim().ToLowerInvariant()}\\{database.Trim().ToLowerInvariant()}";
    }
}

#endif
