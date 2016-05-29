﻿using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Specification.Tests;
using Npgsql;

namespace Npgsql.EntityFrameworkCore.PostgreSQL.FunctionalTests.Utilities
{
    public class NpgsqlTestStore : RelationalTestStore
    {
        public const int CommandTimeout = 30;

        private static int _scratchCount;

        public static NpgsqlTestStore GetOrCreateShared(string name, Action initializeDatabase)
        {
            return new NpgsqlTestStore(name).CreateShared(initializeDatabase);
        }

        /// <summary>
        ///     A non-transactional, transient, isolated test database. Use this in the case
        ///     where transactions are not appropriate.
        /// </summary>
        public static Task<NpgsqlTestStore> CreateScratchAsync(bool createDatabase = true)
        {
            var name = "Npgsql.Scratch_" + Interlocked.Increment(ref _scratchCount);
            return new NpgsqlTestStore(NextScratchDatabaseName()).CreateTransientAsync(createDatabase);
        }

        public static NpgsqlTestStore CreateScratch(bool createDatabase = true)
        {
            return new NpgsqlTestStore(NextScratchDatabaseName()).CreateTransient(createDatabase);
        }

        public static string NextScratchDatabaseName()
            => "Npgsql.Scratch_" + Interlocked.Increment(ref _scratchCount);

        public static string NextScratchConnectionString()
            => CreateConnectionString(NextScratchDatabaseName());

        private string _connectionString;
        private NpgsqlConnection _connection;
        private NpgsqlTransaction _transaction;
        private readonly string _name;
        private bool _deleteDatabase;

        public override string ConnectionString => _connectionString;

        // Use async static factory method
        private NpgsqlTestStore(string name)
        {
            _name = name;
        }

        private NpgsqlTestStore CreateShared(Action initializeDatabase)
        {
            CreateShared(typeof(NpgsqlTestStore).Name + _name, initializeDatabase);

            _connectionString = CreateConnectionString(_name);
            _connection = new NpgsqlConnection(_connectionString);

            _connection.Open();

            _transaction = _connection.BeginTransaction();

            return this;
        }

        /// <summary>
        ///
        /// </summary>
        /// <remarks>
        /// In PostgreSQL (unlike other DBs) a connection is always to a single database - you can't switch
        /// databases retaining the same connection. Therefore, a single SQL script drop and create the database
        /// like with Npgsql, for example.
        /// </remarks>
        /// <param name="name"></param>
        /// <param name="scriptPath"></param>
        /// <param name="recreateIfAlreadyExists"></param>
        public static void CreateDatabase(string name, string scriptPath = null, bool recreateIfAlreadyExists = false)
        {
            // If a script is specified we always drop and recreate an existing database
            if (scriptPath != null) {
                recreateIfAlreadyExists = true;
            }

            using (var master = new NpgsqlConnection(CreateAdminConnectionString()))
            {
                master.Open();

                using (var command = master.CreateCommand())
                {
                    command.CommandTimeout = CommandTimeout;
                    command.CommandText
                        = $@"SELECT COUNT(*) FROM pg_database WHERE datname = '{name}'";

                    var exists = (long)command.ExecuteScalar() > 0;

                    if (exists && recreateIfAlreadyExists)
                    {
                        command.CommandText = $@"DROP DATABASE ""{name}""";
                        command.ExecuteNonQuery();
                    }

                    if (!exists || recreateIfAlreadyExists)
                    {
                        command.CommandText = $@"CREATE DATABASE ""{name}""";
                        command.ExecuteNonQuery();
                    }
                }
            }

            if (scriptPath != null)
            {
                // HACK: Probe for script file as current dir
                // is different between k build and VS run.
                if (File.Exists(@"..\..\" + scriptPath))
                {
                    //executing in VS - so path is relative to bin\<config> dir
                    scriptPath = @"..\..\" + scriptPath;
                }

                var script = File.ReadAllText(scriptPath);

                using (var conn = new NpgsqlConnection(CreateConnectionString(name)))
                {
                    conn.Open();
                    using (var command = new NpgsqlCommand("", conn))
                    {
                        foreach (var batch
                            in
                            new Regex("^GO", RegexOptions.IgnoreCase | RegexOptions.Multiline,
                                TimeSpan.FromMilliseconds(1000.0))
                                .Split(script)) {
                            command.CommandTimeout = 5;
                            command.CommandText = batch;
                            try {
                                command.ExecuteNonQuery();
                            }
                            catch (NpgsqlException e) when (e.InnerException is IOException) {
                                Console.WriteLine("FOO");
                                throw;
                            }
                        }
                    }
                }
            }
        }

        private async Task<NpgsqlTestStore> CreateTransientAsync(bool createDatabase)
        {
            await DeleteDatabaseAsync(_name);

            _connectionString = CreateConnectionString(_name);
            _connection = new NpgsqlConnection(_connectionString);

            if (createDatabase)
            {
                using (var master = new NpgsqlConnection(CreateAdminConnectionString()))
                {
                    await master.OpenAsync();
                    using (var command = master.CreateCommand())
                    {
                        command.CommandText = $@"{Environment.NewLine}CREATE DATABASE ""{_name}""";

                        await command.ExecuteNonQueryAsync();
                    }
                }
                await _connection.OpenAsync();
            }

            _deleteDatabase = createDatabase;
            return this;
        }

        private NpgsqlTestStore CreateTransient(bool createDatabase)
        {
            DeleteDatabase(_name);

            _connectionString = CreateConnectionString(_name);
            _connection = new NpgsqlConnection(_connectionString);

            if (createDatabase)
            {
                using (var master = new NpgsqlConnection(CreateAdminConnectionString()))
                {
                    master.Open();
                    using (var command = master.CreateCommand())
                    {
                        command.CommandText = string.Format(@"{0}CREATE DATABASE ""{1}""", Environment.NewLine, _name);

                        command.ExecuteNonQuery();
                    }
                }
                _connection.Open();
            }

            _deleteDatabase = createDatabase;
            return this;
        }

        private async Task DeleteDatabaseAsync(string name)
        {
            using (var master = new NpgsqlConnection(CreateAdminConnectionString()))
            {
                await master.OpenAsync();

                using (var command = master.CreateCommand())
                {
                    command.CommandTimeout = CommandTimeout; // Query will take a few seconds if (and only if) there are active connections

                    // Kill all connection to the database
                    // TODO: Pre-9.2 PG has column name procid instead of pid
                    command.CommandText = $@"
                      SELECT pg_terminate_backend (pg_stat_activity.pid)
                      FROM pg_stat_activity
                      WHERE pg_stat_activity.datname = '{name}'
                    ";
                    await command.ExecuteNonQueryAsync();

                    command.CommandText = $@"DROP DATABASE IF EXISTS ""{name}""";
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        private void DeleteDatabase(string name)
        {
            using (var master = new NpgsqlConnection(CreateAdminConnectionString()))
            {
                master.Open();

                using (var command = master.CreateCommand())
                {
                    command.CommandTimeout = CommandTimeout; // Query will take a few seconds if (and only if) there are active connections

                    // Kill all connection to the database
                    // TODO: Pre-9.2 PG has column name procid instead of pid
                    command.CommandText = $@"
                      SELECT pg_terminate_backend (pg_stat_activity.pid)
                      FROM pg_stat_activity
                      WHERE pg_stat_activity.datname = '{name}'
                    ";
                    command.ExecuteNonQuery();

                    command.CommandText = $@"DROP DATABASE IF EXISTS ""{name}""";

                    command.ExecuteNonQuery();
                }
            }
        }

        public override DbConnection Connection => _connection;

        public override DbTransaction Transaction => _transaction;

        public async Task<T> ExecuteScalarAsync<T>(string sql, CancellationToken cancellationToken, params object[] parameters)
        {
            using (var command = CreateCommand(sql, parameters))
            {
                return (T)await command.ExecuteScalarAsync(cancellationToken);
            }
        }

        public int ExecuteNonQuery(string sql, params object[] parameters)
        {
            using (var command = CreateCommand(sql, parameters))
            {
                return command.ExecuteNonQuery();
            }
        }

        public Task<int> ExecuteNonQueryAsync(string sql, params object[] parameters)
        {
            using (var command = CreateCommand(sql, parameters))
            {
                return command.ExecuteNonQueryAsync();
            }
        }

        public async Task<IEnumerable<T>> QueryAsync<T>(string sql, params object[] parameters)
        {
            using (var command = CreateCommand(sql, parameters))
            {
                using (var dataReader = await command.ExecuteReaderAsync())
                {
                    var results = Enumerable.Empty<T>();

                    while (await dataReader.ReadAsync())
                    {
                        results = results.Concat(new[] { await dataReader.GetFieldValueAsync<T>(0) });
                    }

                    return results;
                }
            }
        }

        private DbCommand CreateCommand(string commandText, object[] parameters)
        {
            var command = _connection.CreateCommand();

            if (_transaction != null)
            {
                command.Transaction = _transaction;
            }

            command.CommandText = commandText;
            command.CommandTimeout = CommandTimeout;

            for (var i = 0; i < parameters.Length; i++)
            {
                command.Parameters.AddWithValue("p" + i, parameters[i]);
            }

            return command;
        }

        public override void Dispose()
        {
            _transaction?.Dispose();

            _connection.Dispose();

            if (_deleteDatabase)
            {
                DeleteDatabaseAsync(_name).Wait();
            }
        }

        public static string CreateConnectionString(string name)
            => new NpgsqlConnectionStringBuilder(TestEnvironment.DefaultConnection) {
                Database = name
            }.ConnectionString;

        static string CreateAdminConnectionString() => CreateConnectionString("postgres");
    }
}
