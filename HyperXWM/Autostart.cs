using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HyperXWM;

public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string AppName = Path.GetFileNameWithoutExtension(Application.ExecutablePath);

    /// <summary>
    /// Returns true if HKCU Run contains our app.
    /// </summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (key is null)
        {
            return false;
        }

        var value = key.GetValue(AppName) as string;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // value может быть в кавычках и с аргументами; проверим, что путь совпадает
        var exe = Application.ExecutablePath;
        return StartsWithPath(value, exe);
    }

    /// <summary>
    /// Enables or disables autostart by writing/removing HKCU Run value.
    /// </summary>
    public static void SetEnabled(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, true);

        if (!enable)
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
            return;
        }

        var exePath = Quote(Application.ExecutablePath);
        key.SetValue(AppName, exePath, RegistryValueKind.String);
    }

    /// <summary>
    /// Wrap path in quotes if needed.
    /// </summary>
    private static string Quote(string path) => path.Contains(' ') && !path.StartsWith("\"") ? $"\"{path}\"" : path;

    /// <summary>
    /// Check if command starts with exe path.
    /// </summary>
    private static bool StartsWithPath(string command, string exePath)
    {
        var cmd = Trim(command);
        var exe = Trim(exePath);
        return cmd.StartsWith(exe, StringComparison.OrdinalIgnoreCase);

        string Trim(string str) => str.Trim().Trim('"');
    }
}