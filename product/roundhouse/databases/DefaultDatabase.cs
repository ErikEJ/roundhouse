namespace roundhouse.databases
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;
    using Microsoft.Practices.EnterpriseLibrary.TransientFaultHandling;
    using connections;
    using infrastructure.app;
    using infrastructure.app.tokens;
    using infrastructure.extensions;
    using infrastructure.logging;
    using infrastructure.persistence;
    using model;
    using NHibernate;
    using NHibernate.Cfg;
    using NHibernate.Criterion;
    using NHibernate.Tool.hbm2ddl;
    using parameters;
    using sqlsplitters;
    using Environment = System.Environment;
    using Version = model.Version;

    public abstract class DefaultDatabase<DBCONNECTION> : Database
    {
        protected RetryPolicy retry_policy = RetryPolicy.NoRetry;

        public ConfigurationPropertyHolder configuration { get; set; }
        public string server_name { get; set; }
        public string database_name { get; set; }
        public string provider { get; set; }
        public string connection_string { get; set; }
        public string admin_connection_string { get; set; }
        public string roundhouse_schema_name { get; set; }
        public string version_table_name { get; set; }
        public string scripts_run_table_name { get; set; }
        public string scripts_run_errors_table_name { get; set; }
        public string user_name { get; set; }
        public string master_database_name { get; set; }
        public IRepository repository { get; set; }

        public virtual string sql_statement_separator_regex_pattern
        {
            get { return @"(?<KEEP1>^(?:[\s\t])*(?:-{2}).*$)|(?<KEEP1>/{1}\*{1}[\S\s]*?\*{1}/{1})|(?<KEEP1>'{1}(?:[^']|\n[^'])*?'{1})|(?<KEEP1>^|\s)(?<BATCHSPLITTER>\;)(?<KEEP2>\s|$)"; }
        }

        public int command_timeout { get; set; }
        public int admin_command_timeout { get; set; }
        public int restore_timeout { get; set; }
        protected bool split_batches = true;

        public virtual bool split_batch_statements
        {
            get { return split_batches; }
            set { split_batches = value; }
        }

        public virtual bool supports_ddl_transactions
        {
            get { return true; }
        }

        protected IConnection<DBCONNECTION> server_connection;
        protected IConnection<DBCONNECTION> admin_connection;

        private bool disposing;
        private Dictionary<string, ScriptsRun> scripts_cache;

        //this method must set the provider
        public abstract void initialize_connections(ConfigurationPropertyHolder configuration_property_holder);
        public abstract void set_provider();

        public void set_repository()
        {
            NHibernateSessionFactoryBuilder session_factory_builder = new NHibernateSessionFactoryBuilder(configuration);
            Configuration cfg = null;
            ISessionFactory factory = session_factory_builder.build_session_factory(x => { cfg = x; });
            repository = new Repository(factory, cfg);
        }

        public abstract void open_connection(bool with_transaction);
        public abstract void close_connection();
        public abstract void open_admin_connection();
        public abstract void close_admin_connection();

        public abstract void rollback();

        public abstract string create_database_script();
        public abstract string set_recovery_mode_script(bool simple);
        public abstract string restore_database_script(string restore_from_path, string custom_restore_options);
        public abstract string delete_database_script();

        public virtual bool create_database_if_it_doesnt_exist(string custom_create_database_script)
        {
            bool database_was_created = false;
            try
            {
                string create_script = create_database_script();
                if (!string.IsNullOrEmpty(custom_create_database_script))
                {
                    create_script = custom_create_database_script;
                    if (!configuration.DisableTokenReplacement)
                    {
                        create_script = TokenReplacer.replace_tokens(configuration, create_script);
                    }
                }

                if (split_batch_statements)
                {
                    foreach (var sql_statement in StatementSplitter.split_sql_on_regex_and_remove_empty_statements(create_script, sql_statement_separator_regex_pattern))
                    {
                        //should only receive a return value once
                        var return_value = run_sql_scalar_boolean(sql_statement, ConnectionType.Admin);
                        if (return_value != null)
                        {
                            database_was_created = return_value.Value;
                        }
                    }
                }
                else
                {
                    //should only receive a return value once
                    var return_value = run_sql_scalar_boolean(create_script, ConnectionType.Admin);
                    database_was_created = return_value.GetValueOrDefault(false);
                }
            }
            catch (Exception ex)
            {
                Log.bound_to(this).log_a_warning_event_containing(
                    "{0} with provider {1} does not provide a facility for creating a database at this time.{2}{3}",
                    GetType(), provider, Environment.NewLine, ex.to_string());
            }

            return database_was_created;
        }

        private bool? run_sql_scalar_boolean(string sql_to_run, ConnectionType connection_type)
        {
            var return_value = run_sql_scalar(sql_to_run, connection_type);
            if (return_value != null && return_value != DBNull.Value)
            {
                return Convert.ToBoolean(return_value);
            }
            return null;
        }

        public void set_recovery_mode(bool simple)
        {
            try
            {
                run_sql(set_recovery_mode_script(simple), ConnectionType.Admin);
            }
            catch (Exception ex)
            {
                Log.bound_to(this).log_a_warning_event_containing(
                    "{0} with provider {1} does not provide a facility for setting recovery mode to simple at this time.{2}{3}",
                    GetType(), provider, Environment.NewLine, ex.Message);
            }
        }

        public void backup_database(string output_path_minus_database)
        {
            Log.bound_to(this).log_a_warning_event_containing("{0} with provider {1} does not provide a facility for backing up a database at this time.",
                                                              GetType(), provider);
            //todo: backup database is not a script - it is a command
            //Server sql_server =
            //    new Server(new ServerConnection(new SqlConnection(build_connection_string(server_name, database_name))));
            //sql_server.BackupDevices.Add(new BackupDevice(sql_server,database_name));
        }

        public void restore_database(string restore_from_path, string custom_restore_options)
        {
            try
            {
                int current_connection_timeout = command_timeout;
                admin_command_timeout = restore_timeout;
                run_sql(restore_database_script(restore_from_path, custom_restore_options), ConnectionType.Admin);
                admin_command_timeout = current_connection_timeout;
            }
            catch (Exception ex)
            {
                Log.bound_to(this).log_a_warning_event_containing(
                    "{0} with provider {1} does not provide a facility for restoring a database at this time.{2}{3}",
                    GetType(), provider, Environment.NewLine, ex.Message);
            }
        }

        public virtual void delete_database_if_it_exists()
        {
            try
            {
                run_sql(delete_database_script(), ConnectionType.Admin);
            }
            catch (Exception ex)
            {
                Log.bound_to(this).log_an_error_event_containing(
                    "{0} with provider {1} does not provide a facility for deleting a database at this time.{2}{3}",
                    GetType(), provider, Environment.NewLine, ex.Message);
                throw;
            }
        }

        public abstract void run_database_specific_tasks();

        public virtual void create_or_update_roundhouse_tables()
        {
            SchemaUpdate s = new SchemaUpdate(repository.nhibernate_configuration);
            s.Execute(false, true);
        }

        public virtual void run_sql(string sql_to_run, ConnectionType connection_type)
        {
            Log.bound_to(this).log_a_debug_event_containing("[SQL] Running (on connection '{0}'): {1}{2}", connection_type.ToString(), Environment.NewLine, sql_to_run);
            run_sql(sql_to_run, connection_type, null);
        }

        public virtual object run_sql_scalar(string sql_to_run, ConnectionType connection_type)
        {
            Log.bound_to(this).log_a_debug_event_containing("[SQL] Running (on connection '{0}'): {1}{2}", connection_type.ToString(), Environment.NewLine, sql_to_run);
            return run_sql_scalar(sql_to_run, connection_type, null);
        }

        protected abstract void run_sql(string sql_to_run, ConnectionType connection_type, IList<IParameter<IDbDataParameter>> parameters);

        protected abstract object run_sql_scalar(string sql_to_run, ConnectionType connection_type, IList<IParameter<IDbDataParameter>> parameters);

        public void insert_script_run(string script_name, string sql_to_run, string sql_to_run_hash, bool run_this_script_once, long version_id)
        {
            ScriptsRun script_run = new ScriptsRun
                                        {
                                            version_id = version_id,
                                            script_name = script_name,
                                            text_of_script = sql_to_run,
                                            text_hash = sql_to_run_hash,
                                            one_time_script = run_this_script_once
                                        };

            try
            {
                retry_policy.ExecuteAction(() => repository.save_or_update(script_run));
            }
            catch (Exception ex)
            {
                Log.bound_to(this).log_an_error_event_containing(
                    "{0} with provider {1} does not provide a facility for recording scripts run at this time.{2}{3}",
                    GetType(), provider, Environment.NewLine, ex.Message);
                throw;
            }
        }

        public void insert_script_run_error(string script_name, string sql_to_run, string sql_erroneous_part, string error_message, string repository_version,
                                            string repository_path)
        {
            ScriptsRunError script_run_error = new ScriptsRunError
                                                   {
                                                       version = repository_version ?? string.Empty,
                                                       script_name = script_name,
                                                       text_of_script = sql_to_run,
                                                       erroneous_part_of_script = sql_erroneous_part,
                                                       repository_path = repository_path ?? string.Empty,
                                                       error_message = error_message,
                                                   };

            try
            {
                retry_policy.ExecuteAction(() => repository.save_or_update(script_run_error));
            }
            catch (Exception ex)
            {
                Log.bound_to(this).log_an_error_event_containing(
                    "{0} with provider {1} does not provide a facility for recording scripts run errors at this time.{2}{3}",
                    GetType(), provider, Environment.NewLine, ex.Message);
                throw;
            }
        }

        public string get_version(string repository_path)
        {
            string version = "0";

            QueryOver<Version> crit = QueryOver.Of<Version>()
                .Where(x => x.repository_path == (repository_path ?? string.Empty))
                .OrderBy(x => x.entry_date).Desc
                .Take(1);

            IList<Version> items = null;
            try
            {
                items = retry_policy.ExecuteAction(() => repository.get_with_criteria(crit));
            }
            catch (Exception ex)
            {
                Log.bound_to(this).log_a_warning_event_containing("{0} with provider {1} does not provide a facility for retrieving versions at this time.{2}{3}",
                                                                  GetType(), provider, Environment.NewLine, ex.to_string());
            }

            if (items != null && items.Count > 0)
            {
                version = items[0].version;
            }

            return version;
        }

        //get rid of the virtual
        public virtual long insert_version_and_get_version_id(string repository_path, string repository_version)
        {
            long version_id = 0;

            Version version = new Version
                                  {
                                      version = repository_version ?? string.Empty,
                                      repository_path = repository_path ?? string.Empty,
                                  };

            try
            {
                retry_policy.ExecuteAction(() => repository.save_or_update(version));
                version_id = version.id;
            }
            catch (Exception ex)
            {
                Log.bound_to(this).log_an_error_event_containing("{0} with provider {1} does not provide a facility for inserting versions at this time.{2}{3}",
                                                                 GetType(), provider, Environment.NewLine, ex.Message);
                throw;
            }

            return version_id;
        }

        public string get_current_script_hash(string script_name)
        {
            ScriptsRun script = get_from_script_cache(script_name) ?? get_script_run(script_name);
            return script != null ? script.text_hash : string.Empty;
        }

        public bool has_run_script_already(string script_name)
        {
            ScriptsRun script = get_from_script_cache(script_name) ?? get_script_run(script_name);
            return script != null;
        }

        protected IList<ScriptsRun> get_all_scripts()
        {
            return retry_policy.ExecuteAction(() => repository.get_all<ScriptsRun>());
        }

        protected ScriptsRun get_script_run(string script_name)
        {
            QueryOver<ScriptsRun> criteria = QueryOver.Of<ScriptsRun>()
                .Where(x => x.script_name == script_name)
                .OrderBy(x => x.id).Desc
                .Take(1);

            ScriptsRun script = null;
            IList<ScriptsRun> found_items;
            try
            {
                found_items = retry_policy.ExecuteAction(() => repository.get_with_criteria(criteria));
            }
            catch (Exception ex)
            {
                Log.bound_to(this).log_an_error_event_containing(
                    "{0} with provider {1} does not provide a facility for recording scripts run this time.{2}{3}",
                    GetType(), provider, Environment.NewLine, ex.to_string());
                throw;
            }

            if (found_items != null && found_items.Count > 0)
            {
                script = found_items[0];
            }

            return script;
        }

        public virtual void Dispose()
        {
            if (!disposing)
            {
                Log.bound_to(this).log_a_debug_event_containing("Database is disposing normal connection.");
                dispose_connection(server_connection);
                Log.bound_to(this).log_a_debug_event_containing("Database is disposing admin connection.");
                dispose_connection(admin_connection);

                disposing = true;
            }
        }

        private void dispose_connection(IConnection<DBCONNECTION> connection)
        {
            if (connection != null)
            {
                IDbConnection conn = (IDbConnection)connection.underlying_type();
                if (conn != null)
                {
                    if (conn.State != ConnectionState.Closed)
                    {
                        conn.Close();
                    }
                }

                connection.Dispose();
            }
        }

        private ScriptsRun get_from_script_cache(string script_name)
        {
            ensure_script_cache();

            ScriptsRun script;
            if (scripts_cache.TryGetValue(script_name, out script))
            {
                return script;
            }

            return null;
        }

        private void ensure_script_cache()
        {
            if (scripts_cache != null)
            {
                return;
            }

            scripts_cache = new Dictionary<string, ScriptsRun>();

            // latest id overrides possible old one, just like in queries searching for scripts
            foreach (var script in get_all_scripts().OrderBy(x => x.id))
            {
                scripts_cache[script.script_name] = script;
            }
        }
    }
}