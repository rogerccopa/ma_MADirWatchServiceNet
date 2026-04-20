using Serilog;
using System.IO;

namespace MADirWatchServiceNet;

public class ApplicationEventLogger
{
    public ApplicationEventLogger()
	{
		// Ensure logs directory exists and use an absolute path so files are written to the app base folder
		var logsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "Logs");
		Directory.CreateDirectory(logsDirectory);

		// Use a daily-rolling error log file
		string logFilePath = Path.Combine(logsDirectory, "errors-.log");

		Log.Logger = new LoggerConfiguration()
			.MinimumLevel.Error()
			.WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 31)
			.CreateLogger();
	}

	public void LogError(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
			return;

		Log.Error("{Message}", message);
	}

	public void Close()
	{
		Log.CloseAndFlush();
	}
}
