using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Data;
using System.Data.Common;
using System.Dynamic;
using System.Linq;
using System.Text;

namespace Massive {
    public static class ObjectExtensions {
        /// <summary>
        /// Extension method for adding in a bunch of parameters
        /// </summary>
        public static void AddParams(this DbCommand cmd, params object[] args) {
            foreach (var item in args) {
                if (item.GetType().ToString() == "System.Object[]")
                {
                    object[] elems = (object[])item;
                    foreach (object elment in elems) AddParam(cmd, elment);
                }
                else AddParam(cmd, item);
            }
        }
        /// <summary>
        /// Extension for adding single parameter
        /// </summary>
        public static void AddParam(this DbCommand cmd, object item) {
            var p = cmd.CreateParameter();
            p.ParameterName = string.Format("@{0}", cmd.Parameters.Count);
            if (item == null) {
                p.Value = DBNull.Value;
            } else {
                if (item.GetType() == typeof(Guid)) {
                    p.Value = item.ToString();
                    p.DbType = DbType.String;
                    p.Size = 4000;
                } else if (item.GetType() == typeof(ExpandoObject)) {
                    var d = (IDictionary<string, object>)item;
                    p.Value = d.Values.FirstOrDefault();
                } else {
                    p.Value = item;
                }
                if (item.GetType() == typeof(string))
                    p.Size = ((string)item).Length > 4000 ? -1 : 4000;
            }
            cmd.Parameters.Add(p);
        }
        /// <summary>
        /// Turns an IDataReader to a Dynamic list of things
        /// </summary>
        public static List<dynamic> ToExpandoList(this IDataReader rdr) {
            var result = new List<dynamic>();
            while (rdr.Read()) {
                result.Add(rdr.RecordToExpando());
            }
            return result;
        }
        public static dynamic RecordToExpando(this IDataReader rdr) {
            dynamic e = new ExpandoObject();
            var d = e as IDictionary<string, object>;
            for (int i = 0; i < rdr.FieldCount; i++)
                d.Add(rdr.GetName(i), DBNull.Value.Equals(rdr[i]) ? null : rdr[i]);
            return e;
        }
        /// <summary>
        /// Turns the object into an ExpandoObject
        /// </summary>
        public static dynamic ToExpando(this object o) {
            var result = new ExpandoObject();
            var d = result as IDictionary<string, object>; //work with the Expando as a Dictionary
            if (o.GetType() == typeof(ExpandoObject)) return o; //shouldn't have to... but just in case
            if (o.GetType() == typeof(NameValueCollection) || o.GetType().IsSubclassOf(typeof(NameValueCollection))) {
                var nv = (NameValueCollection)o;
                nv.Cast<string>().Select(key => new KeyValuePair<string, object>(key, nv[key])).ToList().ForEach(i => d.Add(i));
            } else {
                var props = o.GetType().GetProperties();
                foreach (var item in props) {
                    d.Add(item.Name, item.GetValue(o, null));
                }
            }
            return result;
        }
        /// <summary>
        /// Turns the object into a Dictionary
        /// </summary>
        public static IDictionary<string, object> ToDictionary(this object thingy) {
            return (IDictionary<string, object>)thingy.ToExpando();
        }
    }
    /// <summary>
    /// A class that wraps your database table in Dynamic Funtime
    /// </summary>
    public class DynamicModel {
        DbProviderFactory _factory;
        string _connectionString;

        string _primaryKeyField;
        string[] _primaryKeySplitted;
        string _tableName;

        public DynamicModel(string connectionStringName = "", string tableName = "", string primaryKeyField = "", char keyColSeparator=',') {
            TableName = tableName == "" ? this.GetType().Name : tableName;
            KeyColSeparator = keyColSeparator;
            PrimaryKeyField = string.IsNullOrEmpty(primaryKeyField) ? "ID" : primaryKeyField;
            if (connectionStringName == "")
                connectionStringName = ConfigurationManager.ConnectionStrings[0].Name;
            var _providerName = "System.Data.SqlClient";
            if (ConfigurationManager.ConnectionStrings[connectionStringName] != null) {
                if (!string.IsNullOrEmpty(ConfigurationManager.ConnectionStrings[connectionStringName].ProviderName))
                    _providerName = ConfigurationManager.ConnectionStrings[connectionStringName].ProviderName;
            } else {
                throw new InvalidOperationException("Can't find a connection string with the name '" + connectionStringName + "'");
            }
            _factory = DbProviderFactories.GetFactory(_providerName);
            _connectionString = ConfigurationManager.ConnectionStrings[connectionStringName].ConnectionString;
        }
        /// <summary>
        /// Enumerates the reader yielding the result - thanks to Jeroen Haegebaert
        /// </summary>
        public virtual IEnumerable<dynamic> Query(string sql, params object[] args) {
            using (var conn = OpenConnection()) {
                var rdr = CreateCommand(sql, conn, args).ExecuteReader();
                while (rdr.Read()) {
                    yield return rdr.RecordToExpando(); ;
                }
            }
        }
        public virtual IEnumerable<dynamic> Query(string sql, DbConnection connection, params object[] args) {
            using (var rdr = CreateCommand(sql, connection, args).ExecuteReader()) {
                while (rdr.Read()) {
                    yield return rdr.RecordToExpando(); ;
                }
            }

        }
        /// <summary>
        /// Returns a single result
        /// </summary>
        public virtual object Scalar(string sql, params object[] args) {
            object result = null;
            using (var conn = OpenConnection()) {
                result = CreateCommand(sql, conn, args).ExecuteScalar();
            }
            return result;
        }
        /// <summary>
        /// Creates a DBCommand that you can use for loving your database.
        /// </summary>
        DbCommand CreateCommand(string sql, DbConnection conn, params object[] args) {
            var result = _factory.CreateCommand();
            result.Connection = conn;
            result.CommandText = sql;
            if (args.Length > 0)
                result.AddParams(args);
            return result;
        }
        /// <summary>
        /// Returns and OpenConnection
        /// </summary>
        public virtual DbConnection OpenConnection() {
            var result = _factory.CreateConnection();
            result.ConnectionString = _connectionString;
            result.Open();
            return result;
        }
        /// <summary>
        /// Builds a set of Insert, Update, Delete commands based on the passed-on objects.
        /// These objects can be POCOs, Anonymous, NameValueCollections, or Expandos. Objects
        /// With a PK property (whatever PrimaryKeyField is set to) will be created at UPDATEs
        /// </summary>
        public virtual List<DbCommand> BuildCommands(params object[] things) {
            var commands = new List<DbCommand>();
            foreach (var item in things) {
                if (HasPrimaryKey(item)) {
                    if (ToBeRemoved(item)) commands.Add(CreateDeleteCommand(byKey: true, args: GetPrimaryKey(item)));
                    else commands.Add(CreateUpdateCommand(item, GetPrimaryKey(item)));
                } else {
                    commands.Add(CreateInsertCommand(item));
                }
            }

            return commands;
        }
        /// <summary>
        /// Executes a set of objects as Insert or Update commands based on their property settings, within a transaction.
        /// These objects can be POCOs, Anonymous, NameValueCollections, or Expandos. Objects
        /// With a PK property (whatever PrimaryKeyField is set to) will be created at UPDATEs
        /// </summary>
        public virtual int Save(params object[] things) {
            var commands = BuildCommands(things);
            return Execute(commands);
        }
        public virtual int Execute(DbCommand command) {
            return Execute(new DbCommand[] { command });
        }
        /// <summary>
        /// Executes a series of DBCommands in a transaction
        /// </summary>
        public virtual int Execute(IEnumerable<DbCommand> commands) {
            var result = 0;
            using (var conn = OpenConnection()) {
                using (var tx = conn.BeginTransaction()) {
                    foreach (var cmd in commands) {
                        cmd.Connection = conn;
                        cmd.Transaction = tx;
                        result += cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
            return result;
        }
        public virtual string PrimaryKeyField { 
            get{
                 return _primaryKeyField; 
               }
 
            set{
                _primaryKeyField=value;
                _primaryKeySplitted = _primaryKeyField.Split(KeyColSeparator).Select(x => x.Trim()).ToArray<string>();
               }
        }
        public virtual char KeyColSeparator { get; set; }
        /// <summary>
        /// Conventionally introspects the object passed in for a field that 
        /// looks like a PK. If you've named your PrimaryKeyField, this becomes easy
        /// </summary>
        public virtual bool HasPrimaryKey(object o)
        {
            IDictionary<string, object> dict = o.ToDictionary();
            foreach (string keyElem in _primaryKeySplitted)
                if (!dict.ContainsKey(keyElem) || dict[keyElem] == null) return false;
            return true;
        }
        /// <summary>
        /// Return true if the object must be removed from the database
        /// The object must provide a boolean 'Remove' to allow this detection
        /// </summary>
        public virtual bool ToBeRemoved(object o)
        {
            IDictionary<string, object> dict = o.ToDictionary();
            if (dict.ContainsKey("Remove"))
            {
                if (dict["Remove"] != null && (bool)dict["Remove"] == true) return true;
                else return false;
            }
            else return false;
        }
        /// <summary>
        /// If the object passed in has a property with the same name as your PrimaryKeyField
        /// it is returned here.
        /// </summary>
        public virtual object GetPrimaryKey(object o)
        {
            int count = 0;
            object[] result;

            result = new object[_primaryKeySplitted.Length];
            for (int i = 0; i < _primaryKeySplitted.Length; i++)
            {
                object keyValElem = null;
                o.ToDictionary().TryGetValue(_primaryKeySplitted[i], out keyValElem);
                if (keyValElem != null) result[i]=keyValElem;
                count++;
            }
            if (count > 0) return result;
            else return null;
        }

        public virtual string TableName
        {
            get
            {
                return _tableName;
            }
            set
            {
                int counter = 0;
                _tableName = string.Empty;
                foreach (string part in value.Split('.'))
                {
                    if (counter > 0) _tableName += ".";
                    _tableName += string.Format("[{0}]", part);
                    counter++;
                }
            }
        }
        /// <summary>
        /// Creates a command for use with transactions - internal stuff mostly, but here for you to play with
        /// </summary>
        public virtual DbCommand CreateInsertCommand(object o) {
            DbCommand result = null;
            var expando = o.ToExpando();
            var settings = (IDictionary<string, object>)expando;
            var sbKeys = new StringBuilder();
            var sbVals = new StringBuilder();
            var stub = "INSERT INTO {0} ({1}) \r\n VALUES ({2})";
            result = CreateCommand(stub, null);
            int counter = 0;
            foreach (var item in settings) {
                sbKeys.AppendFormat("{0},", item.Key);
                sbVals.AppendFormat("@{0},", counter.ToString());
                result.AddParam(item.Value);
                counter++;
            }
            if (counter > 0) {
                var keys = sbKeys.ToString().Substring(0, sbKeys.Length - 1);
                var vals = sbVals.ToString().Substring(0, sbVals.Length - 1);
                var sql = string.Format(stub, TableName, keys, vals);
                result.CommandText = sql;
            } else throw new InvalidOperationException("Can't parse this object to the database - there are no properties set");
            return result;
        }
        /// <summary>
        /// Creates a command for use with transactions - internal stuff mostly, but here for you to play with
        /// </summary>
        public virtual DbCommand CreateUpdateCommand(object o, params object[] key)
        {
            var expando = o.ToExpando();
            var settings = (IDictionary<string, object>)expando;
            var sbKeys = new StringBuilder();
            var stub = "UPDATE {0} SET {1} WHERE ";
            var args = new List<object>();
            var result = CreateCommand(stub, null);

            int counter = 0;

            foreach (var item in settings)
            {
                var val = item.Value;
                bool match =false;

                foreach (string keyElem in _primaryKeySplitted)
                    if(item.Key.Equals(keyElem,StringComparison.CurrentCultureIgnoreCase) && item.Value !=null) match=true;

                if (!match)
                {
                    result.AddParam(val);
                    sbKeys.AppendFormat("[{0}] = @{1}, \r\n", item.Key, counter.ToString());
                    counter++;
                }
            }
            if (counter > 0)
            {
                //add the key
                foreach (object elem in key) result.AddParam(elem);

                //strip the last commas
                var keys = sbKeys.ToString().Substring(0, sbKeys.Length - 4);
                result.CommandText = string.Format(stub, TableName, keys);

                string whereClause="";
                for (int i = 0; i < _primaryKeySplitted.Length; i++)
                {
                    if(i>0) whereClause += " AND ";
                    whereClause = whereClause + string.Format("[{0}] = @{1}\r\n", _primaryKeySplitted[i], counter + i);
                }
                result.CommandText += whereClause;
            }
            else throw new InvalidOperationException("No parsable object was sent in - could not divine any name/value pairs");
            return result;
        }
        /// <summary>
        /// Removes one or more records from the DB according to the passed-in WHERE
        /// </summary>
        public virtual DbCommand  CreateDeleteCommand(string where = "", bool byKey=false, params object[] args)
        {
            var sql = string.Format("DELETE FROM {0} WHERE ",TableName);
            if (byKey) {
                for (int i = 0; i < _primaryKeySplitted.Length; i++)
                {
                    if(i>0) sql += " AND ";
                    sql += string.Format("[{0}] = @{1}\r\n", _primaryKeySplitted[i], i);
                }
            } 
            else if (!string.IsNullOrEmpty(where)) {
                sql += where.Trim().StartsWith("where", StringComparison.CurrentCultureIgnoreCase) ? where : "WHERE " + where;
            }
            return CreateCommand(sql, null, args);
        }
        /// <summary>
        /// Adds a record to the database. You can pass in an Anonymous object, an ExpandoObject,
        /// A regular old POCO, or a NameValueColletion from a Request.Form or Request.QueryString
        /// </summary>
        public virtual object Insert(object o) {
            dynamic result = 0;
            using (var conn = OpenConnection()) {
                var cmd = CreateInsertCommand(o);
                cmd.Connection = conn;
                cmd.ExecuteNonQuery();
                cmd.CommandText = "SELECT @@IDENTITY as newID";
                result = cmd.ExecuteScalar();
            }
            return result;
        }
        /// <summary>
        /// Updates a record in the database. You can pass in an Anonymous object, an ExpandoObject,
        /// A regular old POCO, or a NameValueCollection from a Request.Form or Request.QueryString
        /// </summary>
        public virtual int Update(object o, params object[] key) {
            return Execute(CreateUpdateCommand(o, key));
        }
        /// <summary>
        /// Removes one or more records from the DB according to the passed-in WHERE
        /// </summary>
        public int Delete(bool byKey = false, string where = "", params object[] args) {
            return Execute(CreateDeleteCommand(where: where, byKey: byKey, args: args));
        }
        /// <summary>
        /// Returns all records complying with the passed-in WHERE clause and arguments, 
        /// ordered as specified, limited (TOP) by limit.
        /// </summary>
        public virtual IEnumerable<dynamic> All(string where = "", string orderBy = "", int limit = 0, string columns = "*", params object[] args) {
            string sql = limit > 0 ? "SELECT TOP " + limit + " {0} FROM {1} " : "SELECT {0} FROM {1} ";
            if (!string.IsNullOrEmpty(where))
                sql += where.Trim().StartsWith("where", StringComparison.CurrentCultureIgnoreCase) ? where : "WHERE " + where;
            if (!String.IsNullOrEmpty(orderBy))
                sql += orderBy.Trim().StartsWith("order by", StringComparison.CurrentCultureIgnoreCase) ? orderBy : " ORDER BY " + orderBy;
            return Query(string.Format(sql, columns,TableName), args);
        }

        /// <summary>
        /// Returns a dynamic PagedResult. Result properties are Items, TotalPages, and TotalRecords.
        /// </summary>
        public virtual dynamic Paged(string where = "", string orderBy = "", string columns = "*", int pageSize = 20, int currentPage = 1, params object[] args) {
            dynamic result = new ExpandoObject();
            var countSQL = string.Format("SELECT COUNT({0}) FROM {1}", PrimaryKeyField,TableName);
            if (String.IsNullOrEmpty(orderBy))
                orderBy = PrimaryKeyField;

            if (!string.IsNullOrEmpty(where)) {
                if (!where.Trim().StartsWith("where", StringComparison.CurrentCultureIgnoreCase)) {
                    where = "WHERE " + where;
                }
            }
            var sql = string.Format("SELECT {0} FROM (SELECT ROW_NUMBER() OVER (ORDER BY {2}) AS Row, {0} FROM {3} {4}) AS Paged ", columns, pageSize, orderBy,TableName, where);
            var pageStart = (currentPage - 1) * pageSize;
            sql += string.Format(" WHERE Row > {0} AND Row <={1}", pageStart, (pageStart + pageSize));
            countSQL += where;
            result.TotalRecords = Scalar(countSQL, args);
            result.TotalPages = result.TotalRecords / pageSize;
            if (result.TotalRecords % pageSize > 0)
                result.TotalPages += 1;
            result.Items = Query(string.Format(sql, columns, TableName), args);
            return result;
        }
        /// <summary>
        /// Returns a single row from the database
        /// </summary>
        public virtual dynamic Single(string columns ,params object[] key)
        {
            var sql = string.Format("SELECT {0} FROM {1} WHERE ", columns, TableName);
            for (int i = 0; i < _primaryKeySplitted.Length; i++)
            {
                if(i > 0) sql += " AND ";
                sql = sql + string.Format("[{0}] = @{1}\r\n", _primaryKeySplitted[i], i);
            }
            var items = Query(sql, key).ToList();
            return items.FirstOrDefault();
        }

        public virtual dynamic Single(params object[] key)
        {
            return Single("*" ,key);
        }
    }
}