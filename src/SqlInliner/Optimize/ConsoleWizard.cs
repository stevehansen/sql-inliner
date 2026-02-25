#if !RELEASELIBRARY

using System;
using System.Collections.Generic;

namespace SqlInliner.Optimize;

/// <summary>
/// Abstraction for interactive console I/O to enable testing.
/// </summary>
public interface IConsoleWizard
{
    bool Confirm(string message, bool defaultValue = false);
    int Choose(string message, IReadOnlyList<string> options);
    string? Prompt(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Success(string message);
    void WriteTable(string[] headers, IReadOnlyList<string[]> rows);
    void WaitForEnter(string message);
}

/// <summary>
/// Console-based implementation with colored output.
/// </summary>
public sealed class ConsoleWizard : IConsoleWizard
{
    public bool Confirm(string message, bool defaultValue = false)
    {
        var hint = defaultValue ? "[Y/n]" : "[y/N]";
        Console.Write($"  {message} {hint} ");
        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input))
            return defaultValue;
        return input.Equals("y", StringComparison.OrdinalIgnoreCase) ||
               input.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public int Choose(string message, IReadOnlyList<string> options)
    {
        Console.WriteLine();
        Console.WriteLine($"  {message}");
        for (var i = 0; i < options.Count; i++)
            Console.WriteLine($"    [{i + 1}] {options[i]}");

        while (true)
        {
            Console.Write($"  Choice [1-{options.Count}]: ");
            var input = Console.ReadLine()?.Trim();
            if (int.TryParse(input, out var choice) && choice >= 1 && choice <= options.Count)
                return choice - 1;
            Console.WriteLine("  Invalid choice, try again.");
        }
    }

    public string? Prompt(string message)
    {
        Console.Write($"  {message}: ");
        return Console.ReadLine()?.Trim();
    }

    public void Info(string message)
    {
        Console.WriteLine($"  {message}");
    }

    public void Warn(string message)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  WARNING: {message}");
        Console.ForegroundColor = prev;
    }

    public void Error(string message)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  ERROR: {message}");
        Console.ForegroundColor = prev;
    }

    public void Success(string message)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  {message}");
        Console.ForegroundColor = prev;
    }

    public void WriteTable(string[] headers, IReadOnlyList<string[]> rows)
    {
        var widths = new int[headers.Length];
        for (var i = 0; i < headers.Length; i++)
            widths[i] = headers[i].Length;

        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length && i < widths.Length; i++)
            {
                if (row[i].Length > widths[i])
                    widths[i] = row[i].Length;
            }
        }

        Console.Write("  ");
        for (var i = 0; i < headers.Length; i++)
        {
            Console.Write(headers[i].PadRight(widths[i]));
            if (i < headers.Length - 1) Console.Write("  ");
        }
        Console.WriteLine();

        Console.Write("  ");
        for (var i = 0; i < headers.Length; i++)
        {
            Console.Write(new string('-', widths[i]));
            if (i < headers.Length - 1) Console.Write("  ");
        }
        Console.WriteLine();

        foreach (var row in rows)
        {
            Console.Write("  ");
            for (var i = 0; i < row.Length && i < widths.Length; i++)
            {
                Console.Write(row[i].PadRight(widths[i]));
                if (i < widths.Length - 1) Console.Write("  ");
            }
            Console.WriteLine();
        }
    }

    public void WaitForEnter(string message)
    {
        Console.Write($"  {message}");
        Console.ReadLine();
    }
}

#endif
