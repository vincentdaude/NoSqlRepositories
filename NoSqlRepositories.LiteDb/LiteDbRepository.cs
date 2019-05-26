﻿using LiteDB;
using Newtonsoft.Json;
using NoSqlRepositories.Core;
using NoSqlRepositories.Core.Helpers;
using NoSqlRepositories.Core.NoSQLException;
using NoSqlRepositories.Core.Queries;
using NoSqlRepositories.LiteDb.Helpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NoSqlRepositories.LiteDb
{
    public class LiteDbRepository<T> : RepositoryBase<T> where T : class, IBaseEntity, new()
    {
        #region Members

        private string dbName;
        public override string DatabaseName
        {
            get
            {
                return dbName;
            }
        }

        public override NoSQLEngineType EngineType
        {
            get
            {
                return NoSQLEngineType.LiteDb;
            }
        }

        private string dbPath;
        private readonly LiteDatabase localDb;

        #endregion

        public LiteDbRepository(string dbDirectoryPath, string dbName)
        {
            if (string.IsNullOrWhiteSpace(dbDirectoryPath))
                throw new ArgumentNullException("dbDirectoryPath");

            dbPath = Path.Combine(dbDirectoryPath, dbName + ".db");
            localDb = new LiteDatabase(dbPath);

            this.dbName = dbName;
            CollectionName = typeof(T).Name;

            ConnectAgainToDatabase = () => LoadJSONFile();
        }

        #region INoSQLRepository

        public override async Task Close()
        {
            SaveJSONFile();
            ConnectionOpened = false;
        }

        public override void ConnectAgain()
        {
            ConnectAgainToDatabase();
        }

        public override T GetById(string id)
        {
            CheckOpenedConnection();

            T elt = localDb.GetCollection<T>(CollectionName).FindOne(Query.EQ(nameof(elt.Id), id));

            if(elt != null)
            {
                throw new KeyNotFoundNoSQLException(string.Format("Id '{0}' not found in the repository '{1}'", id, dbPath));
            }

            if (elt.Deleted)
            {
                throw new KeyNotFoundNoSQLException(string.Format("Id '{0}' not found in the repository '{1}'", id, dbPath));
            }

            if (config.IsExpired(id))
            {
                throw new KeyNotFoundNoSQLException(string.Format("Id '{0}' not found in the repository '{1}'", id, dbPath));
            }

            return elt;
        }

        public override IEnumerable<T> GetByIds(IList<string> ids)
        {
            CheckOpenedConnection();

            var elts = new List<T>();

            foreach (string id in ids)
            {
                var elt = TryGetById(id);
                if (elt != null)
                    elts.Add(elt);
            }

            return elts;
        }

        public override InsertResult InsertOne(T entity, InsertMode insertMode)
        {
            CheckOpenedConnection();

            var entitydomain = entity;

            NoSQLRepoHelper.SetIds(entitydomain);

            var updateddate = NoSQLRepoHelper.DateTimeUtcNow();
            var createdDate = NoSQLRepoHelper.DateTimeUtcNow();

            if (!string.IsNullOrEmpty(entity.Id) && Exist(entity.Id))
            {
                // Document already exists
                switch (insertMode)
                {
                    case InsertMode.error_if_key_exists:
                        throw new DupplicateKeyNoSQLException();
                    case InsertMode.erase_existing:
                        createdDate = GetById(entity.Id).SystemCreationDate;
                        Delete(entity.Id);
                        break;
                    case InsertMode.do_nothing_if_key_exists:
                        return InsertResult.not_affected;
                    default:
                        break;
                }
            }

            if (AutoGeneratedEntityDate)
            {
                entitydomain.SystemCreationDate = createdDate;
                entitydomain.SystemLastUpdateDate = updateddate;
            }

            // Clone to not shared reference between App instance and repository persisted value
            // UTC : Ensure to store only utc datetime
            var entityToStore = NewtonJsonHelper.CloneJson(entitydomain, DateTimeZoneHandling.Utc);

            if (localDb.ContainsKey(entity.Id))
                localDb[entity.Id] = entityToStore;
            else
                localDb.Add(entity.Id, entityToStore);

            config.ExpireAt(entity.Id, null);

            SaveJSONFile();

            return InsertResult.inserted;
        }

        public override BulkInsertResult<string> InsertMany(IEnumerable<T> entities, InsertMode insertMode)
        {
            CheckOpenedConnection();

            if (insertMode != InsertMode.db_implementation)
                throw new NotImplementedException();

            var insertResult = new BulkInsertResult<string>();

            foreach (var entity in entities)
            {
                var insertOneResult = InsertOne(entity, insertMode);
                // Normally the entity id has changed
                insertResult[entity.Id] = insertOneResult;
            }

            return insertResult;
        }

        public override bool Exist(string id)
        {
            CheckOpenedConnection();

            try
            {
                GetById(id);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public override UpdateResult Update(T entity, UpdateMode updateMode)
        {
            CheckOpenedConnection();

            if (updateMode == UpdateMode.upsert_if_missing_key)
                throw new NotImplementedException();

            // Update update date
            var date = NoSQLRepoHelper.DateTimeUtcNow();
            var entityToStore = entity;

            entityToStore.SystemLastUpdateDate = date;

            if (!Exist(entity.Id))
            {
                if (updateMode == UpdateMode.error_if_missing_key)
                    throw new KeyNotFoundNoSQLException("Misssing key '" + entity.Id + "'");
                else if (updateMode == UpdateMode.do_nothing_if_missing_key)
                    return UpdateResult.not_affected;
            }

            localDb[entity.Id] = entityToStore;

            config.ExpireAt(entity.Id, null);

            SaveJSONFile();

            return UpdateResult.updated;
        }

        public override long Delete(string id, bool physical)
        {
            CheckOpenedConnection();

            if (Exist(id))
            {
                foreach (var attachmentName in GetAttachmentNames(id))
                {
                    RemoveAttachment(id, attachmentName);
                }

                if (physical)
                {
                    localDb.Remove(id);
                    config.Delete(id);
                }
                else
                {
                    localDb[id].Deleted = true;
                }
                SaveJSONFile();
                return 1;
            }
            else
            {
                return 0;
            }
        }

        public override T TryGetById(string id)
        {
            CheckOpenedConnection();

            try
            {
                return GetById(id);
            }
            catch (KeyNotFoundNoSQLException)
            {
                return null;
            }
        }

        public override int Count()
        {
            CheckOpenedConnection();

            if (this.localDb != null)
            {
                return localDb.Keys.Count(e => !config.IsExpired(e));
            }

            return 0;
        }

        public override void InitCollection(IList<string> indexFieldSelectors)
        {
            // Nothing to do to initialize the collection
        }

        #endregion

        #region INoSQLDB

        public override void ExpireAt(string id, DateTime? dateLimit)
        {
            CheckOpenedConnection();

            config.ExpireAt(id, dateLimit);
            SavedDbConfig();
        }

        public override bool CompactDatabase()
        {
            CheckOpenedConnection();

            if (this.localDb != null)
            {
                foreach (var item in localDb.Values.Where(e => e.Deleted || config.IsExpired(e.Id)))
                {
                    Delete(item.Id, true);
                }
            }
            return true;
        }

        public override long TruncateCollection()
        {
            CheckOpenedConnection();

            var count = localDb.Keys.Count;
            localDb = new ConcurrentDictionary<string, T>();
            config.TruncateCollection();
            SaveJSONFile();
            return count;
        }

        public override void SetCollectionName(string typeName)
        {
            CheckOpenedConnection();

            CollectionName = typeName;
            LoadJSONFile();
        }

        public override void InitCollection()
        {
            CheckOpenedConnection();

            // Autoinit, nothing to do
        }

        public override void UseDatabase(string dbName)
        {
            CheckOpenedConnection();

            throw new NotImplementedException();
        }

        public override bool CollectionExists(bool createIfNotExists)
        {
            CheckOpenedConnection();

            return true;
        }

        public override void DropCollection()
        {
            CheckOpenedConnection();

            localDb.DropCollection(CollectionName);
        }

        #endregion

        #region Private methods

        private void LoadJSONFile()
        {

            ConnectionOpened = true;
        }

        private void SaveJSONFile()
        {
            var settings = new JsonSerializerSettings()
            {
                TypeNameHandling = TypeNameHandling.Auto,
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                // TypeNameAssemblyFormat = System.Runtime.Serialization.Formatters.FormatterAssemblyStyle.Simple
            };

            string content = JsonConvert.SerializeObject(this.localDb, Formatting.Indented, settings);
            if (!Directory.Exists(dbDirectoryPath))
                Directory.CreateDirectory(dbDirectoryPath);

            File.WriteAllText(DbFilePath, content);

            SavedDbConfig();
        }

        private void LoadDbConfigFile()
        {
            if (File.Exists(DbConfigFilePath))
            {
                string content = null;
                try
                {
                    content = File.ReadAllText(DbConfigFilePath);
                    var settings = new JsonSerializerSettings()
                    {
                        TypeNameHandling = TypeNameHandling.Objects,
                        DefaultValueHandling = DefaultValueHandling.Populate
                    };

                    this.config = JsonConvert.DeserializeObject<DbConfiguration>(content, settings);

                    if (this.config == null)
                        this.config = new DbConfiguration(); // Empty file
                }
                catch
                {
                    throw new IOException(string.Format("Cannot read config repository file '{0}'", DbFilePath));
                }
            }
            else
            {
                this.config = new DbConfiguration();
            }
        }

        private void SavedDbConfig()
        {
            var settings = new JsonSerializerSettings()
            {
                TypeNameHandling = TypeNameHandling.Auto,
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore
            };

            string content = JsonConvert.SerializeObject(this.config, Formatting.Indented, settings);
            if (!Directory.Exists(dbDirectoryPath))
                Directory.CreateDirectory(dbDirectoryPath);

            File.WriteAllText(DbConfigFilePath, content);
        }

        #endregion

        #region Attachments

        public override void AddAttachment(string id, Stream fileStream, string contentType, string attachmentName)
        {
            CheckOpenedConnection();

            var entityAttachmentDir = Path.Combine(AttachmentsDirectoryPath, id);
            var attachmentFilePath = Path.Combine(entityAttachmentDir, attachmentName);

            if (!Directory.Exists(entityAttachmentDir))
                Directory.CreateDirectory(entityAttachmentDir);

            using (var file = File.OpenWrite(attachmentFilePath))
            {
                fileStream.CopyTo(file);
            }
        }

        public override void RemoveAttachment(string id, string attachmentName)
        {
            CheckOpenedConnection();

            var entityAttachmentDir = AttachmentsDirectoryPath + "/" + id;
            var attachmentFilePath = entityAttachmentDir + "/" + attachmentName;

            if (!Exist(id))
                throw new KeyNotFoundNoSQLException();

            if (!File.Exists(attachmentFilePath))
                throw new AttachmentNotFoundNoSQLException();

            File.Delete(attachmentFilePath);
        }

        public override Stream GetAttachment(string id, string attachmentName)
        {
            CheckOpenedConnection();

            var entityAttachmentDir = AttachmentsDirectoryPath + "/" + id;
            var attachmentFilePath = entityAttachmentDir + "/" + attachmentName;

            if (!Exist(id))
                throw new KeyNotFoundNoSQLException();

            if (!File.Exists(attachmentFilePath))
                throw new AttachmentNotFoundNoSQLException();

            return File.OpenRead(attachmentFilePath);
        }

        /// <summary>
        /// Return all entities of repository
        /// </summary>
        /// <returns></returns>
        public override IEnumerable<T> GetAll()
        {
            CheckOpenedConnection();

            if (this.localDb != null)
            {
                return localDb.Values.Where(e => !config.IsExpired(e.Id));
            }

            return new List<T>();
        }

        /// <summary>
        /// Return the list of name of all attachements of a given entity
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public override IEnumerable<string> GetAttachmentNames(string id)
        {
            CheckOpenedConnection();

            var entityAttachmentDir = Path.Combine(AttachmentsDirectoryPath, id);

            if (Directory.Exists(entityAttachmentDir))
            {
                var fullFilePath = Directory.GetFiles(entityAttachmentDir);
                return fullFilePath.Select(file => file.Substring(file.LastIndexOf("\\", StringComparison.Ordinal) + 1));
            }
            return new List<string>();
        }

        public override IEnumerable<string> GetKeyByField<TField>(string fieldName, List<TField> values)
        {
            // We need to implement an index to do it.
            throw new NotImplementedException();
        }

        public override IEnumerable<string> GetKeyByField<TField>(string fieldName, TField value)
        {
            // We need to implement an index to do it.
            throw new NotImplementedException();
        }

        #endregion

        #region Queries

        public override IEnumerable<T> DoQuery(NoSqlQuery<T> queryFilters)
        {
            var query = localDb.Values.Select(e => e);

            // Filters :
            if (queryFilters.PostFilter != null)
                query = query.Where(e => queryFilters.PostFilter(e));
            if (queryFilters.Skip > 0)
                query = query.Skip(queryFilters.Skip);
            if (queryFilters.Limit > 0)
                query = query.Take(queryFilters.Limit);

            return query;
        }

        #endregion
    }
}