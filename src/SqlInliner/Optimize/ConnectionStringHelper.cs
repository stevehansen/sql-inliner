#if !RELEASELIBRARY

using Microsoft.Data.SqlClient;

namespace SqlInliner.Optimize;

/// <summary>
/// Resolves connection strings by injecting stored credentials and setting defaults.
/// Replaces the duplicated SqlConnectionStringBuilder normalization blocks across subcommands.
/// </summary>
public static class ConnectionStringHelper
{
    /// <summary>
    /// Resolves a connection string by setting ApplicationName and injecting stored credentials.
    /// </summary>
    /// <param name="connectionString">The raw connection string from CLI or config.</param>
    /// <param name="store">Optional credential store for auto-injection.</param>
    /// <returns>The resolved connection string.</returns>
    public static string Resolve(string connectionString, ICredentialStore? store)
    {
        var csb = new SqlConnectionStringBuilder(connectionString);

        // Set ApplicationName if not already specified
        if (string.IsNullOrEmpty(csb.ApplicationName) || csb.ApplicationName == ".Net SqlClient Data Provider")
            csb.ApplicationName = ThisAssembly.AppName;

        // Already has credentials — return as-is
        if (!string.IsNullOrEmpty(csb.UserID) && !string.IsNullOrEmpty(csb.Password))
            return csb.ToString();

        // Integrated Security explicitly set — return as-is
        if (csb.IntegratedSecurity)
            return csb.ToString();

        // Look up stored credentials
        if (store != null && !string.IsNullOrEmpty(csb.DataSource) && !string.IsNullOrEmpty(csb.InitialCatalog))
        {
            var key = CredentialStoreFactory.BuildKey(csb.DataSource, csb.InitialCatalog);
            var credential = store.Retrieve(key);
            if (credential != null)
            {
                csb.UserID = credential.Username;
                csb.Password = credential.Password;
                return csb.ToString();
            }
        }

        // Fallback: Integrated Security
        csb.IntegratedSecurity = true;
        return csb.ToString();
    }
}

#endif
