﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace NoSqlRepositories.Core
{
    public abstract class AsyncRepositoryBase<T> : IAsyncNoSQLRepository<T> where T : class, IBaseEntity
    {
        public abstract NoSQLEngineType EngineType { get; }

        protected string CollectionName { get; set; }

        public bool AutoGeneratedEntityDate { get; set; } = true;

        public abstract Task AddAttachment(string id, Stream fileStream, string contentType, string attachmentName);

        public abstract Task<bool> CollectionExists(bool createIfNotExists);

        public async Task<long> Delete(string id)
        {
            return await Delete(id, true);
        }

        public abstract Task<long> Delete(string id, bool physical);

        public abstract Task DropCollection();

        public abstract Task<bool> Exist(string id);

        public abstract Task<IList<T>> GetAll();

        public abstract Task<Stream> GetAttachment(string id, string attachmentName);

        public abstract Task<IList<string>> GetAttachmentNames(string id);

        public abstract Task<T> GetById(string id);

        /// <summary>
        /// Get an attachment of an entity in Byte[] or empty if the attachement is not found
        /// </summary>
        /// <param name="id">Id of entity</param>
        /// <param name="attachmentName">Name of attachment</param>
        /// <returns></returns>
        public async Task<byte[]> GetByteAttachment(string id, string attachmentName)
        {
            var result = new Byte[0];

            using (MemoryStream memoryStream = new MemoryStream())
            {
                var attachmentStream = await GetAttachment(id, attachmentName);
                attachmentStream.CopyTo(memoryStream);
                result = memoryStream.ToArray();
            }

            return result;
        }

        public async Task<string> GetCollectionName()
        {
            return CollectionName;
        }

        public abstract Task InitCollection();

        public abstract Task InitCollection(List<System.Linq.Expressions.Expression<Func<T, object>>> indexFieldSelectors);

        public async Task<BulkInsertResult<string>> InsertMany(IEnumerable<T> entities)
        {
            return await InsertMany(entities, InsertMode.db_implementation);
        }

        public abstract Task<BulkInsertResult<string>> InsertMany(IEnumerable<T> entities, InsertMode insertMode);

        public async Task<InsertResult> InsertOne(T entity)
        {
            return await InsertOne(entity, InsertMode.error_if_key_exists);
        }

        public abstract Task<InsertResult> InsertOne(T entity, InsertMode insertMode);

        public abstract Task RemoveAttachment(string id, string attachmentName);

        public abstract Task SetCollectionName(string typeName);

        public abstract Task<long> TruncateCollection();

        public abstract Task<T> TryGetById(string id);

        public async Task<UpdateResult> Update(T entity)
        {
            return await Update(entity, UpdateMode.db_implementation);
        }

        public abstract Task<UpdateResult> Update(T entity, UpdateMode updateMode);

        public abstract Task UseDatabase(string dbName);

        public abstract Task<List<T>> GetByField<TField>(string fieldName, List<TField> values);

        public abstract Task<List<T>> GetByField<TField>(string fieldName, TField value);

        public abstract Task<List<string>> GetKeyByField<TField>(string fieldName, List<TField> values);

        public abstract Task<List<string>> GetKeyByField<TField>(string fieldName, TField value);

    }
}
