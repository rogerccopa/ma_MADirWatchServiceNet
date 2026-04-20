using Microsoft.Extensions.Options;
using System.Collections.Specialized;
using System.Data;
using System.Diagnostics;
using System.Text;

namespace MADirWatchServiceNet;

public class DirWatchWorker : BackgroundService, IHostedLifecycleService
{
	private readonly ILogger<DirWatchWorker> _logger;
	private readonly DirWatchSettings _dirWatchSettings;
	private readonly ApplicationEventLogger _appEventLogger;
	private const string SERVICE_NAME = "MADirWatchServiceNet";

	private DatabaseInfo authDbServer = new DatabaseInfo();
	private Dictionary<string, DatabaseInfo> queueDbServers = new Dictionary<string, DatabaseInfo>();
	private int checkForFileReleaseEverySeconds = 30;
    private DatabaseInfo clientDbInfo = new DatabaseInfo();
	private FileSystemWatcher fileSystemWatcher;
	private Dictionary<string, int> uploads = new Dictionary<string, int>();
	private string watchDirectoryPath = string.Empty;
	private int fileReleaseMaxHours = 3;
    private Object locker = new Object();

    public DirWatchWorker(ILogger<DirWatchWorker> logger, IOptions<DirWatchSettings> options, ApplicationEventLogger appEventLogger)
	{
		_logger = logger;
		_dirWatchSettings = options.Value;
		_appEventLogger = appEventLogger ?? throw new ArgumentNullException(nameof(appEventLogger));
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try
		{
			fileSystemWatcher = new FileSystemWatcher();
			fileSystemWatcher.Path = watchDirectoryPath;
			fileSystemWatcher.InternalBufferSize = 32 * 1024; // 32KB
			fileSystemWatcher.IncludeSubdirectories = true;
			fileSystemWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.LastAccess;
			fileSystemWatcher.Created += new FileSystemEventHandler(fileSystemWatcher_CreatedOrChanged);
			fileSystemWatcher.Changed += new FileSystemEventHandler(fileSystemWatcher_CreatedOrChanged);
			fileSystemWatcher.Error += new ErrorEventHandler(fileSystemWatcher_Error);

			// Begin watching.
			fileSystemWatcher.EnableRaisingEvents = true;

			_logger.LogInformation("Watcher active.");

			// Keep the worker alive until the host requests shutdown.
			await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
		}
		catch (Exception ex)
		{
			_appEventLogger.LogError(ex.Message);
		}
	}

	public Task StartingAsync(CancellationToken cancellationToken)
	{
		_logger.LogInformation("Starting {serviceName}", SERVICE_NAME);

		// change service default directory from C:\Windows\System32 to <AssembyLocationDirectory>
		Environment.CurrentDirectory = System.AppDomain.CurrentDomain.BaseDirectory;

		try
		{
			string authDbConnString = _dirWatchSettings.ConnStrings.AuthDb;
			string queueDbConnString = _dirWatchSettings.ConnStrings.QueueDb;
			string clientDbConnString = _dirWatchSettings.ConnStrings.ClientDb;

			authDbServer = DatabaseInfo.ParseConnectionString(authDbConnString);
			authDbServer.Id = "auth";

			DatabaseInfo queueDbServer = DatabaseInfo.ParseConnectionString(queueDbConnString);
			queueDbServers.Add(queueDbServer.Id, queueDbServer);

			clientDbInfo = DatabaseInfo.ParseConnectionString(clientDbConnString);

			checkForFileReleaseEverySeconds = _dirWatchSettings.Check4FileReleaseEverySeconds;
			watchDirectoryPath = _dirWatchSettings.WatchDirectory;

			if (string.IsNullOrEmpty(watchDirectoryPath))
			{
				throw new Exception("WatchDirectory is not set in appsettings.json");
			}
			if (string.IsNullOrEmpty(queueDbConnString))
			{
				throw new Exception("Env variable ConnStrings.QueueDB is not set");
			}

			_logger.LogInformation("Watching directory: {watchDirectoryPath}", watchDirectoryPath);
			return Task.CompletedTask;
		}
		catch (Exception exception)
		{
			_appEventLogger.LogError(exception.Message);
			throw;
		}
	}

	void fileSystemWatcher_CreatedOrChanged(object sender, FileSystemEventArgs e)
	{
		string dbname = "";
		string filename = "";

		if (e.ChangeType == WatcherChangeTypes.Created || e.ChangeType == WatcherChangeTypes.Changed)
		{
			if (e.FullPath.ToLower().Contains(@"\store\"))
			{
				filename = Path.GetFileName(e.Name).ToLower();

				int pos = e.FullPath.ToLower().IndexOf("macm");
				if (pos > 0)
				{
					dbname = e.FullPath.Substring(pos, 8);

					pos = dbname.IndexOf("\\");
					if (pos > 0) { dbname = dbname.Substring(0, pos); }
				}
			}
		}

		if (filename.Length == 0 || dbname.Length == 0) { return; }
		if (uploads.ContainsKey(dbname + filename)) { return; }

		uploads.Add(dbname + filename, 0);

		string lastUpdateTime = DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss tt");
		string triggerActivationTime = DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss tt");

		Dictionary<string, DatabaseInfo> logDbServers = new Dictionary<string, DatabaseInfo>();
		foreach (DatabaseInfo dbServer in queueDbServers.Values)
		{
			logDbServers.Add(dbServer.Id, dbServer.Copy());
		}
		logDbServers.Add(authDbServer.Id, authDbServer.Copy());
		logDbServers.Add("customer", new DatabaseInfo()
		{
			Id = "customer",
			Server = triggerActivationTime,
			Database = dbname,
			Username = filename,
			Password = lastUpdateTime
		});
		// log file upload
		ThreadPool.QueueUserWorkItem(LogDirectoryFileEvent, logDbServers);

		Dictionary<string, DatabaseInfo> dbServers = new Dictionary<string, DatabaseInfo>();
		foreach (DatabaseInfo dbServer in queueDbServers.Values)
		{
			dbServers.Add(dbServer.Id, dbServer.Copy());
		}
		dbServers.Add(authDbServer.Id, authDbServer.Copy());
		dbServers.Add("customer", new DatabaseInfo()
		{
			Id = "customer",
			Server = this.checkForFileReleaseEverySeconds.ToString(),
			Database = dbname,
			Username = filename,
			Password = e.FullPath
		});
		// fire trigger
		ThreadPool.QueueUserWorkItem(LaunchCustomerTrigger, dbServers);
	}

	private void LaunchCustomerTrigger(object callParameter)
	{
		Dictionary<string, DatabaseInfo> dbServers = (Dictionary<string, DatabaseInfo>)callParameter;

		DatabaseInfo authDbServer = dbServers["auth"].Copy();
		string dbname = dbServers["customer"].Database;
		string filename = dbServers["customer"].Username;
		string filePath = dbServers["customer"].Password;
		int secondsFileReleaseCheck = int.Parse(dbServers["customer"].Server);
		dbServers.Clear();
		
		DataAccess datax = new DataAccess();
		string query = "SELECT TOP 1 * FROM UserBase with(NOLOCK) WHERE dbname='" + dbname + "' AND active=1";
		DataTable tblUserBase = datax.GetRecords(query, authDbServer.ConnectionString);

		if (datax.HasError)
		{
			_appEventLogger.LogError(datax.ErrorMessage);
			RemoveUploadFromList(dbname + filename);
			return;
		}
		if (tblUserBase.Rows.Count == 0)
		{
			RemoveUploadFromList(dbname + filename);
			return;
		}

		DataRow rowUserBase = tblUserBase.Rows[0];
		DatabaseInfo customerQueueDbServer;

		if (!queueDbServers.TryGetValue(rowUserBase["qServer"].ToString().Trim().ToLower(), out customerQueueDbServer))
		{
			_appEventLogger.LogError($"Queue server={rowUserBase["qServer"]} not found in app settings.");
			RemoveUploadFromList(dbname + filename);
			return;
		}

		DatabaseInfo customerDb = new DatabaseInfo();
		customerDb.Server = rowUserBase["dbServer"].ToString();
		customerDb.Port = int.Parse(rowUserBase["dbPort"].ToString());
		customerDb.Database = rowUserBase["dbName"].ToString();
		customerDb.Username = clientDbInfo.Username;
		customerDb.Password = clientDbInfo.Password;
		customerDb.UsernameOld = rowUserBase["userAccess"].ToString();
		customerDb.PasswordOld = rowUserBase["password"].ToString();

		query = "SELECT uid, originTableName as fileName, destUid, destTableName, originSuccess " +
				"FROM ETriggers with(NOLOCK) " +
				"WHERE originUid = 0 AND originTableName = '" + filename.Replace("'", "''") + "'";

		DataTable tblEventTriggers = datax.GetRecords(query, customerDb.ConnectionString);

		if (datax.HasError)
		{
			_appEventLogger.LogError(datax.ErrorMessage);
			RemoveUploadFromList(dbname + filename);
			return;
		}

		if (tblEventTriggers.Rows.Count == 0)
		{
			RemoveUploadFromList(dbname + filename);
			return;
		}

		Stopwatch stopwatch = Stopwatch.StartNew();
		bool fileIsLocked = true;

		while (fileIsLocked)
		{
			FileStream fileStream = null;

			try
			{
				FileInfo fileInfo = new FileInfo(filePath);

				fileStream = fileInfo.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);

				fileIsLocked = false;
			}
			catch (Exception)
			{
				stopwatch.Stop();
				if (stopwatch.Elapsed.TotalHours > fileReleaseMaxHours) { break; }
				stopwatch.Start();

				Thread.Sleep(secondsFileReleaseCheck * 1000);
			}
			finally
			{
				if (fileStream != null) { fileStream.Close(); }
			}
		}

		stopwatch.Stop();
		RemoveUploadFromList(dbname + filename);

		// file was locked for too long
		if (fileIsLocked)
		{
			string detail = "File was not released after " + fileReleaseMaxHours + " hours";
			query = "INSERT INTO FileTrigger(account,filename,triggerId,timeUpdate,details,triggerActivated) " +
					"VALUES('" + dbname + "','" + filename + "',0,null,'" + detail + "',null)";

			datax.InsertAndGetUid(query, customerQueueDbServer.ConnectionString);
			return;
		}

		int processStarterType = 6; // File-Drop
		int processSTarterId = 0;

		foreach (DataRow rowEventTrigger in tblEventTriggers.Rows)
		{
			if (rowEventTrigger["originSuccess"].ToString().Equals("2")) { continue; } // trigger is Paused

			int destUid = int.Parse(rowEventTrigger["destUid"].ToString());
			int lastLogUid = 0;
			int schedType = 0; // default Import.
			int storefrontUid = 0;
			string provider = "";
			string protocol = "";
			string location = "";
			string ftpFolder = "";
			string schedFilename = "";
			DataTable tblWizards = null;
			processSTarterId = int.Parse(rowEventTrigger["uid"].ToString());

			if (rowEventTrigger["destTableName"].ToString() == "Imports")
			{
				query = "SELECT * FROM Imports WHERE uid = " + destUid;

				DataTable tblImport = datax.GetRecords(query, customerDb.ConnectionString);

				if (datax.HasError) { _appEventLogger.LogError(datax.ErrorMessage); }
				else if (tblImport.Rows.Count == 0) { _appEventLogger.LogError("Import not found uid=" + destUid); }
				else
				{
					storefrontUid = int.Parse("0" + tblImport.Rows[0]["storefrontUid"].ToString());
					provider = tblImport.Rows[0]["name"].ToString();
					protocol = tblImport.Rows[0]["protocol"].ToString();
					location = tblImport.Rows[0]["location"].ToString();
					ftpFolder = tblImport.Rows[0]["ftpFolder"].ToString();
					schedFilename = tblImport.Rows[0]["filename"].ToString();

					// check if Import is already waiting in Queue
					query = $@"	SELECT TOP 1 uid 
									FROM InScheduleQ with(NOLOCK)
									WHERE dbName = '{customerDb.Database}' AND schedType = 0 AND localId = {destUid} AND state = 0";
					DataTable tblSameImportInQueue = datax.GetRecords(query, customerQueueDbServer.ConnectionString);
					if (datax.HasError)
					{
						_appEventLogger.LogError(datax.ErrorMessage);
						continue;
					}

					if (tblSameImportInQueue.Rows.Count > 0) { continue; }  // Import is already in Queue

					query = "INSERT INTO ImportLogs(importUid, occurrence, errorCode, state, ProcessStarterType, ProcessStarterId) " +
							$"VALUES({destUid}, getdate(), 'Processing...', 1, {processStarterType}, {processSTarterId})";

					lastLogUid = datax.InsertAndGetUid(query, customerDb.ConnectionString);

					if (datax.HasError) { _appEventLogger.LogError(datax.ErrorMessage); }
				}
			}
			else if (rowEventTrigger["destTableName"].ToString() == "Channels")
			{
				schedType = 1;

				if (rowEventTrigger["destUid"].ToString().Equals("0")) // KeywordControl campaigns
				{
					query = "SELECT W.uid,W.HasGeneratedAd,C.uid as channelUid,C.uname as clientEmail,C.partner," +
							"K.userId as mccEmail, K.password as mccPassword, A.accountCustomerId as clientCustomerId " +
							"FROM KeywordWizard W " +
							"INNER JOIN Channels C ON C.uid = W.channelUid " +
							"INNER JOIN KeywordId K ON K.partner = C.partner " +
							"INNER JOIN KeywordAccount A ON A.accountId = C.uname " +
							"WHERE W.hasGeneratedAd = 2 AND C.channelType = 5 AND C.active = 1";

					tblWizards = datax.GetRecords(query, customerDb.ConnectionString);

					if (datax.HasError) { _appEventLogger.LogError(datax.ErrorMessage); }

					if (tblWizards.Rows.Count == 0) { continue; }
				}
				else
				{
					query = "SELECT * FROM Channels WHERE uid = " + rowEventTrigger["destUid"].ToString();

					DataTable tblChannel = datax.GetRecords(query, customerDb.ConnectionString);

					if (datax.HasError) { _appEventLogger.LogError(datax.ErrorMessage); }
					else if (tblChannel.Rows.Count == 0) { _appEventLogger.LogError("Channel not found uid=" + destUid); }
					else
					{
						provider = tblChannel.Rows[0]["name"].ToString();
						protocol = tblChannel.Rows[0]["protocol"].ToString();
						location = tblChannel.Rows[0]["location"].ToString();
						ftpFolder = tblChannel.Rows[0]["ftpFolder"].ToString();
						schedFilename = tblChannel.Rows[0]["filename"].ToString();

						query = "INSERT INTO ChannelLogs(channelUid, occurrence, errorCode, state, ProcessStarterType, ProcessStarterId) " +
								$"VALUES({destUid}, getdate(), 'Processing...', 1, {processStarterType}, {processSTarterId})";

						lastLogUid = datax.InsertAndGetUid(query, customerDb.ConnectionString);

						if (datax.HasError) { _appEventLogger.LogError(datax.ErrorMessage); }
					}
				}
			}

			if (lastLogUid > 0)
			{
				query = "INSERT INTO InScheduleQ(dbServer, dbName, uname, pword, schedType, version, schedGroupId, storeName," +
						"provider, localId, localLogId, storefrontUid, protocol, location, ftpFolder, filename, storage, " +
						"state, ftpServer, ProcessStarterType, ProcessStarterId) " +
						"VALUES('" + customerDb.Server + "','" + customerDb.Database + "','" + customerDb.UsernameOld + "','" +
						customerDb.PasswordOld + "'," + schedType + ",'" +
						rowUserBase["currentVersion"].ToString().Replace(".", "") + "'," +
						rowUserBase["schedGroupId"].ToString() + ",'MADirWatchService','" + provider.Replace("'", "''") + "'," +
						destUid + "," + lastLogUid + "," + storefrontUid + ",'" + protocol + "','" + location + "','" +
						ftpFolder + "','" + schedFilename + "','" + rowUserBase["storage"].ToString() + "\\sync" +
						"',0,'" + rowUserBase["ftpServer"].ToString() + "'," + processStarterType + "," + processSTarterId + ")";

				datax.InsertAndGetUid(query, customerQueueDbServer.ConnectionString);

				if (datax.HasError) { _appEventLogger.LogError(datax.ErrorMessage); }
			}
			else
			{
				if (tblWizards != null)
				{
					StringBuilder queries = new StringBuilder();

					foreach (DataRow rowWizard in tblWizards.Rows)
					{
						query = "INSERT INTO InScheduleK(dbserver,dbname,uname,pword," +
								"mccEmail, mccPassword, clientEmail, clientCustomerId," +
								"schedtype,tableAction,version,schedgroupid,channelUid, " +
								"channelLogUid, provider, partner, tableUid, state) " +
								"VALUES('" + customerDb.Server + "','" + customerDb.Database + "','" +
								customerDb.UsernameOld + "','" + customerDb.PasswordOld + "','" +
								rowWizard["mccEmail"].ToString() + "','" +
								rowWizard["mccPassword"].ToString() + "','" +
								rowWizard["clientEmail"].ToString() + "','" +
								rowWizard["clientCustomerId"].ToString() + "',3,1,'" +
								rowUserBase["currentVersion"].ToString().Replace(".", "") + "'," +
								rowUserBase["schedGroupId"].ToString() + "," +
								rowWizard["channelUid"].ToString() + ",0,'EventTrigger','" +
								rowWizard["partner"].ToString() + "'," +
								rowWizard["uid"].ToString() + ",0)" + ";";

						queries.Append(query);
					}

					datax.UpdateRecords(queries.ToString(), customerQueueDbServer.ConnectionString);

					if (datax.HasError) { _appEventLogger.LogError(datax.ErrorMessage); }
					else
					{
						queries = new StringBuilder();

						foreach (DataRow rowWizard in tblWizards.Rows)
						{
							query = "UPDATE KeywordWizard SET HasGeneratedAd = 1 WHERE uid=" + rowWizard["uid"].ToString() + ";";
							queries.Append(query);
						}

						datax.UpdateRecords(queries.ToString(), customerDb.ConnectionString);

						if (datax.HasError) { _appEventLogger.LogError(datax.ErrorMessage); }
					}
				}
			}
		}
	}

	private void LogDirectoryFileEvent(object callParameter)
	{
		Dictionary<string, DatabaseInfo> dbServers = (Dictionary<string, DatabaseInfo>)callParameter;

		DatabaseInfo authDbServer = dbServers["auth"];
		string dbname = dbServers["customer"].Database;
		string filename = dbServers["customer"].Username;
		string lastUpdateTime = dbServers["customer"].Password;
		string triggerActivationTime = dbServers["customer"].Server;

		
		DataAccess datax = new DataAccess();

		string query = "SELECT TOP 1 dbServer,dbPort,dbName,userAccess,password,qServer " +
						"FROM UserBase with(NOLOCK) " +
						"WHERE dbname = '" + dbname + "' AND active=1";

		DataTable tblUserBase = datax.GetRecords(query, authDbServer.ConnectionString);

		if (datax.HasError)
		{
			_appEventLogger.LogError(datax.ErrorMessage);
			dbServers.Clear();
			return;
		}

		if (tblUserBase.Rows.Count == 0) { dbServers.Clear(); return; }

		DataRow rowUserBase = tblUserBase.Rows[0];
		DatabaseInfo customerDb = new DatabaseInfo();
		customerDb.Server = rowUserBase["dbServer"].ToString();
		customerDb.Port = int.Parse(rowUserBase["dbPort"].ToString());
		customerDb.Database = rowUserBase["dbName"].ToString();
		customerDb.Username = clientDbInfo.Username;
		customerDb.Password = clientDbInfo.Password;
		customerDb.UsernameOld = rowUserBase["userAccess"].ToString();
		customerDb.PasswordOld = rowUserBase["password"].ToString();

		query = "SELECT TOP 1 uid FROM ETriggers with(NOLOCK) WHERE originUid = 0 AND originTableName = '" + filename + "'";

		DataTable tblEventTrigger = datax.GetRecords(query, customerDb.ConnectionString);

		if (datax.HasError)
		{
			_appEventLogger.LogError(datax.ErrorMessage);
			dbServers.Clear();
			return;
		}

		int triggerUid = 0;
		string detail = "file not found in eTriggers.";

		if (tblEventTrigger.Rows.Count > 0)
		{
			triggerUid = int.Parse(tblEventTrigger.Rows[0]["uid"].ToString());
			detail = "Successfully completed.";
		}

		if (triggerUid == 0) { return; }

		DatabaseInfo customerQueueDbServer;

		if (!dbServers.TryGetValue(rowUserBase["qServer"].ToString().Trim().ToLower(), out customerQueueDbServer))
		{
			_appEventLogger.LogError("Queue server=" + rowUserBase["qServer"].ToString() + " not found in app settings.");
			dbServers.Clear();
			return;
		}

		query = "INSERT INTO FileTrigger(account,filename,triggerId,timeUpdate,details,triggerActivated) " +
				"VALUES('" + dbname + "','" + filename + "'," + triggerUid + "," +
				(lastUpdateTime.Length > 0 ? "'" + lastUpdateTime + "'" : "null") + ",'" + detail + "'," +
				(triggerActivationTime.Length > 0 ? "'" + triggerActivationTime + "'" : "null") + ")";

		datax.InsertAndGetUid(query, customerQueueDbServer.ConnectionString);

		if (datax.HasError) { _appEventLogger.LogError(datax.ErrorMessage); }
		dbServers.Clear();
	}

	void fileSystemWatcher_Error(object sender, ErrorEventArgs e)
	{
		if (e.GetException().GetType() == typeof(InternalBufferOverflowException))
		{
			
			_appEventLogger.LogError("Error: Internal buffer overflow. Msg:" + e.GetException().Message);
		}
		else
		{
			
			_appEventLogger.LogError("Error: Watched directory not accessible. Msg:" + e.GetException().Message);
		}

		ResetDirectoryWatcher();
	}

	void ResetDirectoryWatcher()
	{
		
		int iMaxAttempts = 10;
		int iTimeOut = 30000;
		int i = 0;

		while ((!Directory.Exists(fileSystemWatcher.Path) || fileSystemWatcher.EnableRaisingEvents == false) && i < iMaxAttempts)
		{
			i += 1;
			try
			{
				fileSystemWatcher.EnableRaisingEvents = false;
				if (!Directory.Exists(fileSystemWatcher.Path))
				{
					_appEventLogger.LogError("Directory Inaccesible " + fileSystemWatcher.Path);
					System.Threading.Thread.Sleep(iTimeOut);
				}

				if (Directory.Exists(fileSystemWatcher.Path))
				{
					string directoryPath = fileSystemWatcher.Path;
					// ReInitialize the Component
					fileSystemWatcher.Dispose();
					fileSystemWatcher = null;
					fileSystemWatcher = new System.IO.FileSystemWatcher();
					((System.ComponentModel.ISupportInitialize)(fileSystemWatcher)).BeginInit();
					fileSystemWatcher.EnableRaisingEvents = true;
					fileSystemWatcher.Path = directoryPath;
					fileSystemWatcher.InternalBufferSize = 32 * 1024; // 32KB
					fileSystemWatcher.IncludeSubdirectories = true;
					fileSystemWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.LastAccess;
					fileSystemWatcher.Created += new FileSystemEventHandler(fileSystemWatcher_CreatedOrChanged);
					fileSystemWatcher.Changed += new FileSystemEventHandler(fileSystemWatcher_CreatedOrChanged);
					fileSystemWatcher.Error += new ErrorEventHandler(fileSystemWatcher_Error);
					((System.ComponentModel.ISupportInitialize)(fileSystemWatcher)).EndInit();
				}
			}
			catch (Exception ex)
			{
				_appEventLogger.LogError("Error trying to reset service. Msg:" + ex.Message + " " + ex.StackTrace);
				fileSystemWatcher.EnableRaisingEvents = false;
				System.Threading.Thread.Sleep(iTimeOut);
			}
		}
	}

	private void RemoveUploadFromList(string key)
	{
		lock (locker) { uploads.Remove(key); }
	}

	public Task StartedAsync(CancellationToken cancellationToken)
	{
		_logger.LogInformation("{serviceName} started", SERVICE_NAME);
		return Task.CompletedTask;
	}

	public Task StoppingAsync(CancellationToken cancellationToken)
	{
		_logger.LogInformation("Stopping {serviceName}", SERVICE_NAME);
		_appEventLogger.Close();
		return Task.CompletedTask;
	}

	public Task StoppedAsync(CancellationToken cancellationToken)
	{
		_logger.LogInformation("{serviceName} stopped", SERVICE_NAME);
		return Task.CompletedTask;
	}
}
