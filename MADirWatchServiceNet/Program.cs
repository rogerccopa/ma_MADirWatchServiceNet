using MADirWatchServiceNet;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(args);

// Read DirWatchSettings from configuration, in order:
// 1. appsettings.json
// 2. appsettings.{Environment}.json
// 3. User secrets
// 4. Environment variables
// 5. Command-line args
// and bind it to the DirWatchSettings class
builder.Services.AddOptions<DirWatchSettings>()
	.BindConfiguration("DirWatchSettings")
	.Validate(
		validation: opts => !string.IsNullOrEmpty(opts.WatchDirectory), 
		failureMessage: "WatchDirectory must be specified in configuration.");

if (WindowsServiceHelpers.IsWindowsService())
{
	builder.Services.AddWindowsService(options =>
	{
		options.ServiceName = "MADirWatchServiceNet";
	});
}

// Register application-level event logger for DI
builder.Services.AddSingleton<ApplicationEventLogger>();

builder.Services.AddHostedService<DirWatchWorker>();

var host = builder.Build();
host.Run();
