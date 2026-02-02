using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Serilog;

namespace HyperXWM;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var assemblyPath = Path.GetDirectoryName(AppContext.BaseDirectory)
                           ?? AppDomain.CurrentDomain.BaseDirectory;
        var logPath = Path.Combine(assemblyPath, "logs", "log-.txt");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day, // new file every day.
                retainedFileCountLimit: 7, // keep logs only for one week.
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}