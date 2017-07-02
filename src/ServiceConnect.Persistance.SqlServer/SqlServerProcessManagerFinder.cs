﻿//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using System.Xml.XPath;
using Common.Logging;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;
using IsolationLevel = System.Data.IsolationLevel;

namespace ServiceConnect.Persistance.SqlServer
{
    /// <summary>
    /// Sql Server implementation of IProcessManagerFinder.
    /// </summary>
    public class SqlServerProcessManagerFinder : IProcessManagerFinder
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(SqlServerProcessManagerFinder));
        private readonly string _connectionString;
        private readonly int _commandTimeout = 30;
        private const string TimeoutsTableName = "Timeouts";

        /// <summary>
        /// Default constructor
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="databaseName"></param>
        public SqlServerProcessManagerFinder(string connectionString, string databaseName)
        {
            _connectionString = connectionString;
        }

        /// <summary>
        /// Constructor allows passing <see cref="commandTimeout"/>.
        /// Used primarily for testing.
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="databaseName"></param>
        /// <param name="commandTimeout"></param>
        public SqlServerProcessManagerFinder(string connectionString, string databaseName, int commandTimeout)
        {
            _connectionString = connectionString;
            _commandTimeout = commandTimeout;
        }

        public event TimeoutInsertedDelegate TimeoutInserted;

        /// <summary>
        /// Find existing instance of ProcessManager
        /// FindData() and UpdateData() are part of the same transaction.
        /// FindData() opens new connection and transaction. 
        /// UPDLOCK is placed onf the relevant row to prevent reads until the transaction is commited in UpdateData
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public IPersistanceData<T> FindData<T>(IProcessManagerPropertyMapper mapper, Message message) where T : class, IProcessManagerData
        {
            var mapping = mapper.Mappings.FirstOrDefault(m => m.MessageType == message.GetType()) ??
                          mapper.Mappings.First(m => m.MessageType == typeof(Message));

            string tableName = typeof(T).Name;

            var sbXPath = new StringBuilder();
            sbXPath.Append("(/" + tableName);
            foreach (var prop in mapping.PropertiesHierarchy.Reverse())
            {
                sbXPath.Append("/" + prop.Key);
            }
            sbXPath.Append(")[1]");

            XPathExpression xPathExpression;
            try
            {
                xPathExpression = XPathExpression.Compile(sbXPath.ToString());
            }
            catch (XPathException ex)
            {
                Logger.ErrorFormat("Error compiling xpath expression. {0}", ex.Message);
                throw;
            }

            // Message Propery Value
            object msgPropValue = mapping.MessageProp.Invoke(message);

            SqlServerData<T> result = null;

            if (!GetTableNameExists(tableName))
                return null;

            using (var sqlConnection = new SqlConnection(_connectionString))
            {
                sqlConnection.Open();

                using (var command = new SqlCommand())
                {
                    command.Connection = sqlConnection;
                    command.CommandTimeout = _commandTimeout;
                    command.CommandText = string.Format(@"SELECT * FROM {0} WHERE DataXml.value('{1}', 'nvarchar(max)') = @val", tableName, xPathExpression.Expression);
                    command.Parameters.Add(new SqlParameter {ParameterName = "@val", Value = msgPropValue});
                    
                    try
                    {
                        var reader = command.ExecuteReader(CommandBehavior.SingleResult);

                        if (reader.HasRows)
                        {
                            reader.Read();

                            var serializer = new XmlSerializer(typeof (T));
                            object res;
                            using (TextReader r = new StringReader(reader["DataXml"].ToString()))
                            {
                                res = serializer.Deserialize(r);
                            }

                            result = new SqlServerData<T>
                            {
                                Id = (Guid) reader["Id"],
                                Data = (T) res,
                                Version = (int) reader["Version"]
                            };
                        }

                        reader.Dispose();
                    }
                    finally
                    {
                        sqlConnection.Close();
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Create new instance of ProcessManager
        /// When multiple threads try to create new ProcessManager instance, only the first one is allowed. 
        /// All subsequent threads will update data instead.
        /// </summary>
        /// <param name="data"></param>
        public void InsertData(IProcessManagerData data)
        {
            string tableName = GetTableName(data);

            var sqlServerData = new SqlServerData<IProcessManagerData>
            {
                Data = data,
                Version = 1,
                Id = data.CorrelationId
            };

            var xmlSerializer = new XmlSerializer(data.GetType());
            var sww = new StringWriter();
            XmlWriter writer = XmlWriter.Create(sww);
            xmlSerializer.Serialize(writer, data);
            var dataXml = sww.ToString();

            using (var sqlConnection = new SqlConnection(_connectionString))
            {
                sqlConnection.Open();

                using (var dbTransaction = sqlConnection.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    // Insert if doesn't exist, else update (only the first one is allowed)
                    string upsertSql = string.Format(@"if exists (select * from {0} with (updlock,serializable) WHERE Id = @Id)
                                                    begin
                                                        UPDATE {0}
		                                                SET DataXml = @DataXml, Version = @Version 
		                                                WHERE Id = @Id
                                                    end
                                                else
                                                    begin
                                                        INSERT {0} (Id, Version, DataXml)
                                                        VALUES (@Id,@Version,@DataXml)
                                                    end", tableName);


                    using (var command = new SqlCommand(upsertSql))
                    {
                        command.Connection = sqlConnection;
                        command.Transaction = dbTransaction;
                        command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = data.CorrelationId;
                        command.Parameters.Add("@Version", SqlDbType.Int).Value = sqlServerData.Version;
                        command.Parameters.Add("@DataXml", SqlDbType.Xml).Value = dataXml;

                        try
                        {
                            command.ExecuteNonQuery();
                            dbTransaction.Commit();
                        }
                        catch
                        {
                            dbTransaction.Rollback();
                            throw;
                        }
                        finally
                        {
                            sqlConnection.Close();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Update data of existing ProcessManager and completes transaction opened by FindData().
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        public void UpdateData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData
        {
            string tableName = GetTableName(data.Data);

            var sqlServerData = (SqlServerData<T>)data;
            int currentVersion = sqlServerData.Version;

            var xmlSerializer = new XmlSerializer(data.Data.GetType());
            var sww = new StringWriter();
            XmlWriter writer = XmlWriter.Create(sww);
            xmlSerializer.Serialize(writer, data.Data);
            var dataXml = sww.ToString();

            string sql = string.Format(@"UPDATE {0} SET DataXml = @DataXml, Version = @NewVersion WHERE Id = @Id AND Version = @CurrentVersion", tableName);

            int result;
            using (var sqlConnection = new SqlConnection(_connectionString))
            {
                sqlConnection.Open();

                using (var command = new SqlCommand(sql))
                {
                    command.Connection = sqlConnection;
                    command.CommandTimeout = _commandTimeout;
                    command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = sqlServerData.Id;
                    command.Parameters.Add("@DataXml", SqlDbType.Xml).Value = dataXml;
                    command.Parameters.Add("@CurrentVersion", SqlDbType.Int).Value = currentVersion;
                    command.Parameters.Add("@NewVersion", SqlDbType.Int).Value = ++currentVersion;

                    try
                    {
                        result = command.ExecuteNonQuery();
                    }
                    finally
                    {
                        sqlConnection.Close();
                    }
                }
            }

            if (result == 0)
                throw new ArgumentException(string.Format("Possible Concurrency Error. ProcessManagerData with CorrelationId {0} and Version {1} could not be updated.", sqlServerData.Data.CorrelationId, sqlServerData.Version));
        }

        /// <summary>
        /// Removes existing instance of ProcessManager from the database and 
        /// completes transaction opened by FindData().
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        public void DeleteData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData
        {
            string tableName = GetTableName(data.Data);

            var sqlServerData = (SqlServerData<T>)data;

            string sql = string.Format(@"DELETE FROM {0} WHERE Id = @Id", tableName);

            using (var sqlConnection = new SqlConnection(_connectionString))
            {
                sqlConnection.Open();

                using (var command = new SqlCommand(sql))
                {
                    command.Connection = sqlConnection;
                    command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = sqlServerData.Id;

                    try
                    {
                        command.ExecuteNonQuery();
                    }
                    finally
                    {
                        sqlConnection.Close();
                    }
                }
            }
        }

        public void InsertTimeout(TimeoutData timeoutData)
        {
            using (var sqlConnection = new SqlConnection(_connectionString))
            {
                sqlConnection.Open();

                using (var cmd = new SqlCommand())
                {
                    cmd.Connection = sqlConnection;
                    cmd.CommandText = string.Format("IF NOT EXISTS( SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{0}') ", TimeoutsTableName) +
                        string.Format("CREATE TABLE {0} (Id uniqueidentifier NOT NULL, ProcessManagerId uniqueidentifier NOT NULL, Destination varchar(250) NOT NULL, Time DateTime NOT NULL, Locked bit, Headers text);", TimeoutsTableName);
                    cmd.ExecuteNonQuery();
                }

                using (var dbTran = sqlConnection.BeginTransaction(IsolationLevel.Serializable))
                {
                    // Insert if doesn't exist, else update (only the first one is allowed)
                    string sql = string.Format(@"INSERT {0} (Id, ProcessManagerId, Destination, Time, Locked, Headers)
                                                 VALUES (@Id, @ProcessManagerId, @Destination, @Time, @Locked, @Headers)", TimeoutsTableName);

                    using (var cmd = new SqlCommand(sql))
                    {
                        cmd.Connection = sqlConnection;
                        cmd.Transaction = dbTran;
                        cmd.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = timeoutData.Id;
                        cmd.Parameters.Add("@ProcessManagerId", SqlDbType.UniqueIdentifier).Value = timeoutData.ProcessManagerId;
                        cmd.Parameters.Add("@Destination", SqlDbType.VarChar).Value = timeoutData.Destination;
                        cmd.Parameters.Add("@Time", SqlDbType.DateTime).Value = timeoutData.Time;
                        cmd.Parameters.Add("@Locked", SqlDbType.Bit).Value = timeoutData.Locked;
                        cmd.Parameters.Add("@Headers", SqlDbType.Text).Value = JsonConvert.SerializeObject(timeoutData.Headers);

                        cmd.ExecuteNonQuery();
                        dbTran.Commit();
                    }
                }
            }

            if (TimeoutInserted != null)
            {
                TimeoutInserted(timeoutData.Time);
            }
        }

        public TimeoutsBatch GetTimeoutsBatch()
        {
            var retval = new TimeoutsBatch { DueTimeouts = new List<TimeoutData>() };

            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();

                // Make sure table exists
                using (var cmd = new SqlCommand())
                {
                    cmd.Connection = connection;
                    cmd.CommandText = string.Format("IF NOT EXISTS( SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{0}') ", TimeoutsTableName) +
                        string.Format("CREATE TABLE {0} (Id uniqueidentifier NOT NULL, ProcessManagerId uniqueidentifier NOT NULL, Destination varchar(250) NOT NULL, Time DateTime NOT NULL, Locked bit, Headers text);", TimeoutsTableName);
                    cmd.ExecuteNonQuery();
                }

                using (var dbTran = connection.BeginTransaction(IsolationLevel.Serializable))
                {
                    var utcNow = DateTime.UtcNow;

                    // Get timeouts due
                    string querySql = string.Format("SELECT Id, ProcessManagerId, Destination, Time, Locked, Headers  FROM  [dbo].[{0}] WHERE Locked = 0 AND Time <= @Time",TimeoutsTableName);
                    using (var cmd = new SqlCommand(querySql, connection, dbTran))
                    {
                        cmd.Parameters.AddWithValue("@Time", utcNow);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var td = new TimeoutData
                                {
                                    Destination = reader["Destination"].ToString(),
                                    Id = Guid.Parse(reader["Id"].ToString()),
                                    Headers = JsonConvert.DeserializeObject<IDictionary<string, object>>(reader["Headers"].ToString()),
                                    ProcessManagerId = Guid.Parse(reader["ProcessManagerId"].ToString()),
                                    Time = (DateTime) reader["Time"]
                                };
                                retval.DueTimeouts.Add(td);
                            }
                        }
                    }

                    // Lock records with timout due
                    string updateSql = string.Format("UPDATE  [dbo].[{0}] SET Locked = 1  WHERE Locked = 0 AND Time <= @Time", TimeoutsTableName);
                    using (var cmd = new SqlCommand(updateSql, connection, dbTran))
                    {
                        cmd.Parameters.AddWithValue("@Time", utcNow);
                        cmd.ExecuteNonQuery();
                    }

                    // Get next query time
                    var nextQueryTime = DateTime.MaxValue;
                    string nextQueryTimeSql = string.Format("SELECT Time  FROM  [dbo].[{0}] WHERE Time > @Time", TimeoutsTableName);
                    using (var cmd = new SqlCommand(nextQueryTimeSql, connection, dbTran))
                    {
                        cmd.Parameters.AddWithValue("@Time", utcNow);

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                if ((DateTime)reader["Time"] < nextQueryTime)
                                {
                                    nextQueryTime = (DateTime)reader["Time"];
                                }
                            }
                        }
                    }

                    if (nextQueryTime == DateTime.MaxValue)
                    {
                        nextQueryTime = utcNow.AddMinutes(1);
                    }

                    retval.NextQueryTime = nextQueryTime;

                    dbTran.Commit();
                }
            }

            return retval;
        }

        public void RemoveDispatchedTimeout(Guid id)
        {
            string sql = string.Format("DELETE FROM  [dbo].[{0}] WHERE Locked = 1 AND Id = @Id", TimeoutsTableName);

            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var cmd = new SqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@Id", id);
                cmd.CommandType = CommandType.Text;
                cmd.ExecuteNonQuery();
            }
        }

        #region Private Methods

        private bool GetTableNameExists(string tableName)
        {
            bool retval = false;

            // Create table if doesn't exist
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand())
                {
                    command.Connection = connection;
                    command.CommandText = string.Format("SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{0}'", tableName);
                    var result = command.ExecuteScalar();

                    if (null != result && (int)result == 1)
                        retval = true;
                }
            }

            return retval;
        }

        private string GetTableName<T>(T data) where T : class, IProcessManagerData
        {
            Type typeParameterType = data.GetType();
            var tableName = typeParameterType.Name;

            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();

                // Create table if doesn't exist
                using (var command = new SqlCommand())
                {
                    command.Connection = connection;
                    command.CommandText = string.Format("IF NOT EXISTS( SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{0}') ", tableName) +
                        string.Format("CREATE TABLE {0} (Id uniqueidentifier NOT NULL, Version int NOT NULL, DataXml xml NULL);", tableName);
                    command.ExecuteNonQuery();
                }

                // Create index if doesn't exist
                using (var command = new SqlCommand())
                {
                    command.Connection = connection;
                    command.CommandText = string.Format("IF NOT EXISTS( SELECT 1 FROM sys.indexes WHERE name='ClusteredIndex_{0}' AND object_id = OBJECT_ID('{0}')) ", tableName) +
                        string.Format("CREATE UNIQUE CLUSTERED INDEX  [ClusteredIndex_{0}] ON [dbo].[{0}] ([Id] ASC)", tableName);
                    command.ExecuteNonQuery();
                }
                connection.Close();
            }

            return tableName;
        }

        #endregion
    }
}
