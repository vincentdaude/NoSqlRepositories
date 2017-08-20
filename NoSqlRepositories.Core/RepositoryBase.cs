﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace NoSqlRepositories.Core
{
    public abstract class RepositoryBase<T> : INoSQLRepository<T> where T : class, IBaseEntity
    {
        public abstract string DatabaseName { get; }

        public abstract NoSQLEngineType EngineType { get; }

        protected string CollectionName { get; set; }

        public bool AutoGeneratedEntityDate { get; set; } = true;

        public abstract void AddAttachment(string id, Stream fileStream, string contentType, string attachmentName);

        public abstract bool CollectionExists(bool createIfNotExists);

        public long Delete(string id)
        {
            return Delete(id, true);
        }
        
        public abstract long Delete(string id, bool physical);

        public abstract bool CompactDatabase();

        public abstract void ExpireAt(string id, DateTime? dateLimit);

        public abstract void DropCollection();

        public abstract bool Exist(string id);

        public abstract IEnumerable<T> GetAll();

        public abstract Stream GetAttachment(string id, string attachmentName);

        public abstract IEnumerable<string> GetAttachmentNames(string id);

        public abstract T GetById(string id);

        /// <summary>
        /// Get an attachment of an entity in Byte[] or empty if the attachement is not found
        /// </summary>
        /// <param name="id">Id of entity</param>
        /// <param name="attachmentName">Name of attachment</param>
        /// <returns></returns>
        public byte[] GetByteAttachment(string id, string attachmentName)
        {
            var reuslt = new Byte[0];

            using (MemoryStream memoryStream = new MemoryStream())
            {
                var attachmentStream = GetAttachment(id, attachmentName);
                attachmentStream.CopyTo(memoryStream);
                reuslt = memoryStream.ToArray();
            }

            return reuslt;
        }

        public string GetCollectionName()
        {
            return CollectionName;
        }

        public abstract void InitCollection();

        public abstract void InitCollection(IList<System.Linq.Expressions.Expression<Func<T, object>>> indexFieldSelectors);

        public BulkInsertResult<string> InsertMany(IEnumerable<T> entities)
        {
            return InsertMany(entities, InsertMode.db_implementation);
        }

        public abstract BulkInsertResult<string> InsertMany(IEnumerable<T> entities, InsertMode insertMode);

        public InsertResult InsertOne(T entity)
        {
            return InsertOne(entity, InsertMode.error_if_key_exists);
        }

        public abstract InsertResult InsertOne(T entity, InsertMode insertMode);

        public abstract void RemoveAttachment(string id, string attachmentName);

        public abstract void SetCollectionName(string typeName);

        public abstract long TruncateCollection();

        public abstract T TryGetById(string id);

        public abstract IList<T> GetByIds(IList<string> ids);

        public UpdateResult Update(T entity)
        {
            return Update(entity, UpdateMode.db_implementation);
        }

        public abstract UpdateResult Update(T entity, UpdateMode updateMode);

        public abstract void UseDatabase(string dbName);

        public abstract IEnumerable<T> GetByField<TField>(string fieldName, List<TField> values);

        public abstract IEnumerable<T> GetByField<TField>(string fieldName, TField value);

        public abstract IEnumerable<string> GetKeyByField<TField>(string fieldName, List<TField> values);

        public abstract IEnumerable<string> GetKeyByField<TField>(string fieldName, TField value);

        public abstract int Count();
    }
}
