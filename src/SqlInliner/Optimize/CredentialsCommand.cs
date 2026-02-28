#if !RELEASELIBRARY

using System;
using System.CommandLine;
using System.Text;

namespace SqlInliner.Optimize;

/// <summary>
/// System.CommandLine subcommand definition for managing stored credentials.
/// Provides add, list, and remove sub-commands for the OS credential store.
/// </summary>
public static class CredentialsCommand
{
    public static Command Create()
    {
        var command = new Command("credentials", "Manage stored database credentials in the OS credential store");

        command.Add(CreateAddCommand());
        command.Add(CreateListCommand());
        command.Add(CreateRemoveCommand());

        return command;
    }

    private static Command CreateAddCommand()
    {
        var serverOption = new Option<string>("--server", "-s")
        {
            Description = "SQL Server hostname or instance name",
            Required = true,
        };
        var databaseOption = new Option<string>("--database", "-d")
        {
            Description = "Database name",
            Required = true,
        };
        var usernameOption = new Option<string?>("--username", "-u")
        {
            Description = "Username (prompted if not provided)",
        };

        var addCommand = new Command("add", "Store credentials for a server/database pair")
        {
            serverOption,
            databaseOption,
            usernameOption,
        };

        addCommand.SetAction(parseResult =>
        {
            var server = parseResult.GetValue(serverOption)!;
            var database = parseResult.GetValue(databaseOption)!;
            var username = parseResult.GetValue(usernameOption);

            var store = CredentialStoreFactory.Create(out var warning);
            if (store == null)
            {
                Console.Error.WriteLine($"Error: {warning ?? "Credential store is not available."}");
                return;
            }

            if (string.IsNullOrEmpty(username))
            {
                Console.Write("Username: ");
                username = Console.ReadLine();
                if (string.IsNullOrEmpty(username))
                {
                    Console.Error.WriteLine("Error: Username is required.");
                    return;
                }
            }

            var password = ReadPassword("Password: ");
            if (string.IsNullOrEmpty(password))
            {
                Console.Error.WriteLine("Error: Password is required.");
                return;
            }

            var key = CredentialStoreFactory.BuildKey(server, database);
            store.Store(key, username, password);
            Console.WriteLine($"Credential stored for {server}\\{database}.");
        });

        return addCommand;
    }

    private static Command CreateListCommand()
    {
        var listCommand = new Command("list", "List all stored credentials (passwords are never shown)");

        listCommand.SetAction(_ =>
        {
            var store = CredentialStoreFactory.Create(out var warning);
            if (store == null)
            {
                Console.Error.WriteLine($"Error: {warning ?? "Credential store is not available."}");
                return;
            }

            var entries = store.List();
            if (entries.Count == 0)
            {
                Console.WriteLine("No stored credentials.");
                return;
            }

            // Format as table
            var serverWidth = "Server".Length;
            var databaseWidth = "Database".Length;
            var usernameWidth = "Username".Length;

            var rows = new (string Server, string Database, string Username)[entries.Count];
            for (var i = 0; i < entries.Count; i++)
            {
                var parts = entries[i].Key.Split('\\', 2);
                var server = parts.Length > 0 ? parts[0] : entries[i].Key;
                var database = parts.Length > 1 ? parts[1] : "";
                rows[i] = (server, database, entries[i].Username);

                if (server.Length > serverWidth) serverWidth = server.Length;
                if (database.Length > databaseWidth) databaseWidth = database.Length;
                if (entries[i].Username.Length > usernameWidth) usernameWidth = entries[i].Username.Length;
            }

            Console.WriteLine($"  {"Server".PadRight(serverWidth)}  {"Database".PadRight(databaseWidth)}  {"Username".PadRight(usernameWidth)}");
            Console.WriteLine($"  {new string('-', serverWidth)}  {new string('-', databaseWidth)}  {new string('-', usernameWidth)}");

            foreach (var row in rows)
                Console.WriteLine($"  {row.Server.PadRight(serverWidth)}  {row.Database.PadRight(databaseWidth)}  {row.Username.PadRight(usernameWidth)}");
        });

        return listCommand;
    }

    private static Command CreateRemoveCommand()
    {
        var serverOption = new Option<string>("--server", "-s")
        {
            Description = "SQL Server hostname or instance name",
            Required = true,
        };
        var databaseOption = new Option<string>("--database", "-d")
        {
            Description = "Database name",
            Required = true,
        };

        var removeCommand = new Command("remove", "Remove stored credentials for a server/database pair")
        {
            serverOption,
            databaseOption,
        };

        removeCommand.SetAction(parseResult =>
        {
            var server = parseResult.GetValue(serverOption)!;
            var database = parseResult.GetValue(databaseOption)!;

            var store = CredentialStoreFactory.Create(out var warning);
            if (store == null)
            {
                Console.Error.WriteLine($"Error: {warning ?? "Credential store is not available."}");
                return;
            }

            var key = CredentialStoreFactory.BuildKey(server, database);
            if (store.Remove(key))
                Console.WriteLine($"Credential removed for {server}\\{database}.");
            else
                Console.WriteLine($"No credential found for {server}\\{database}.");
        });

        return removeCommand;
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        var password = new StringBuilder();

        while (true)
        {
            var keyInfo = Console.ReadKey(intercept: true);

            if (keyInfo.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (keyInfo.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Remove(password.Length - 1, 1);
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(keyInfo.KeyChar))
            {
                password.Append(keyInfo.KeyChar);
                Console.Write('*');
            }
        }

        return password.ToString();
    }
}

#endif
