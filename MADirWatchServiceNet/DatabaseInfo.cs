namespace MADirWatchServiceNet;

public class DatabaseInfo
{
	public string Id = "";
	public string Server = "";
	public int Port = DEFAULT_PORT;
	public string Database = "";
	public string Username = "";
	public string Password = "";
	public string UsernameOld = "";
	public string PasswordOld = "";

	private const int DEFAULT_PORT = 1433;

	public string ConnectionString
	{
		get
		{
			return $"Server={Server},{Port};Database={Database};User ID={Username};Password={Password};TrustServerCertificate=True";
		}
	}

	public DatabaseInfo Copy()
	{
		DatabaseInfo dbInfo = new DatabaseInfo();
		dbInfo.Id = this.Id;
		dbInfo.Server = this.Server;
		dbInfo.Port = this.Port;
		dbInfo.Database = this.Database;
		dbInfo.Username = this.Username;
		dbInfo.Password = this.Password;
		dbInfo.UsernameOld = this.UsernameOld;
		dbInfo.PasswordOld = this.PasswordOld;

		return dbInfo;
	}

	public static DatabaseInfo ParseConnectionString(string connectionString)
	{
		string server = string.Empty;
		int port = DEFAULT_PORT;
		string database = string.Empty;
		string username = string.Empty;
		string password = string.Empty;

		string[] parts = connectionString.Split(';');
		foreach (string part in parts)
		{
			string[] keyValue = part.Split('=');
			if (keyValue.Length == 2)
			{
				string key = keyValue[0].Trim().ToLower();
				string value = keyValue[1].Trim();
				switch (key)
				{
					case "server":
						string[] serverParts = value.Split(',');
						server = serverParts[0];
						if (serverParts.Length > 1 && int.TryParse(serverParts[1], out int portNumber))
						{
							port = portNumber;
						}
						break;
					case "database":
						database = value;
						break;
					case "user id":
						username = value;
						break;
					case "password":
						password = value;
						break;
				}
			}
		}

		return new DatabaseInfo
		{
			Id = server,
			Server = server,
			Port = port,
			Database = database,
			Username = username,
			Password = password
		};
	}
}
