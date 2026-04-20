using System.ComponentModel.DataAnnotations;

namespace MADirWatchServiceNet;

public class DirWatchSettings
{
	[Required]
	public string WatchDirectory { get; set; } = string.Empty;

	[Range(1, 9)]
	public int Check4FileReleaseEverySeconds { get; set; }

	public ConnectionStrings ConnStrings { get; set; } = new();
}

public class ConnectionStrings
{
	[Required]
	public string AuthDb { get; set; } = string.Empty;

	[Required]
	public string QueueDb { get; set; } = string.Empty;

	[Required]
	public string ClientDb { get; set; } = string.Empty;
}
