using Microsoft.Data.SqlClient;
using System.Data;

namespace MADirWatchServiceNet;

public class DataAccess
{
	public bool HasError = false;
	public string ErrorMessage = "";

	public DataTable GetRecords(string query, string dbConnStr)
	{
		HasError = false;

		SqlDataAdapter sqlDA = new SqlDataAdapter(query, dbConnStr);
		DataTable tblResult = new DataTable();

		try
		{
			sqlDA.Fill(tblResult);
			sqlDA.Dispose();
		}
		catch (Exception exception)
		{
			HasError = true;
			ErrorMessage = exception.Message + " Qry=" + query;

			tblResult.Dispose();
			tblResult = null;
		}

		return tblResult;
	}

	public int UpdateRecords(string query, string dbConnStr)
	{
		HasError = false;
		int recordsAffected = 0;

		using (SqlConnection conn = new SqlConnection(dbConnStr))
		using (SqlCommand cmd = new SqlCommand(query, conn))
		{
			try
			{
				conn.Open();
				recordsAffected = cmd.ExecuteNonQuery();
			}
			catch (Exception exception)
			{
				HasError = true;
				ErrorMessage = exception.Message + " Qry=" + query;
				conn.Close();
			}
			finally
			{
				if (conn.State == ConnectionState.Open) { conn.Close(); }
			}
		}

		return recordsAffected;
	}

	public int InsertAndGetUid(string insertQuery, string dbConnStr)
	{
		HasError = false;
		int lastRecordUid = 0;

		insertQuery = "SET NOCOUNT ON;" + insertQuery + ";SELECT scope_identity()";

		using (SqlConnection conn = new SqlConnection(dbConnStr))
		using (SqlCommand cmd = new SqlCommand(insertQuery, conn))
		{
			SqlDataReader sqlReader = null;

			try
			{
				conn.Open();
				sqlReader = cmd.ExecuteReader();

				if (sqlReader.Read())
				{
					lastRecordUid = int.Parse(sqlReader[0].ToString());
				}
			}
			catch (Exception exception)
			{
				HasError = true;
				ErrorMessage = exception.Message + " Qry=" + insertQuery;
			}
			finally
			{
				if (sqlReader != null) { sqlReader.Close(); }
			}
		}

		return lastRecordUid;
	}
}
