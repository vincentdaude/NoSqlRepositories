﻿using MvvX.Plugins.CouchBaseLite;
using MvvX.Plugins.CouchBaseLite.Database;
using MvvX.Plugins.CouchBaseLite.Documents;
using MvvX.Plugins.CouchBaseLite.Queries;
using MvvX.Plugins.CouchBaseLite.Views;
using Newtonsoft.Json.Linq;
using NoSqlRepositories.Core;
using NoSqlRepositories.Core.Helpers;
using NoSqlRepositories.Core.NoSQLException;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using MvvX.Plugins.CouchBaseLite.Storages;
using System.Threading.Tasks;

namespace NoSqlRepositories.MvvX.CouchBase.Pcl
{
    /// <summary>
    /// The repository can contains instance of subtype of the type T and handle polymorphism. For that
    /// the subclasses of class T must be declared in the attribute PolymorphicTypes
    /// Limitations : couchbase repository doesn't handle polymorphism in attribute's entity of type List, Dictionary...
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class CouchBaseRepository<T> : RepositoryBase<T> where T : class, IBaseEntity
    {
        /// <summary>
        /// Contains list of subclasses of the class T to handle polymorphism during deserialization
        /// </summary>
        public IDictionary<string, Type> PolymorphicTypes { get; } = new Dictionary<string, Type>();

        protected ICouchBaseLite couchBaseLite;
        protected IDatabase database;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="couchBaseLite"></param>
        /// <param name="fileStore"></param>
        public CouchBaseRepository(ICouchBaseLite couchBaseLite, string dbName)
        {
            Construct(couchBaseLite, StorageTypes.Sqlite, dbName);
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="couchBaseLite"></param>
        /// <param name="fileStore"></param>
        public CouchBaseRepository(ICouchBaseLite couchBaseLite, StorageTypes storage, string dbName)
        {
            Construct(couchBaseLite, storage, dbName);
        }


        private void Construct(ICouchBaseLite couchBaseLite, StorageTypes storage, string dbName)
        {
            if (couchBaseLite == null)
                throw new ArgumentNullException("couchBaseLite");
            
            this.couchBaseLite = couchBaseLite;
            this.CollectionName = typeof(T).Name;

            ConnectToDatabase(storage, dbName);

            CreateAllDocView();
        }

        private void ConnectToDatabase(StorageTypes storage, string dbName)
        {
            var databaseOptions = this.couchBaseLite.CreateDatabaseOptions();
            databaseOptions.Create = true;
            databaseOptions.StorageType = storage;

            this.database = this.couchBaseLite.CreateConnection(dbName, databaseOptions);

            if (this.database == null)
                throw new NullReferenceException("CreateConnection returned no connection");
        }


        public override T GetById(string id)
        {
            //JsonConvert.DeserializeObject
            var documentObjet = this.database.GetDocument(getInternalCBLId(id));
            if (documentObjet == null || string.IsNullOrEmpty(documentObjet.CurrentRevisionId) || documentObjet.Deleted)
            {
                throw new KeyNotFoundNoSQLException();
            }

            T entity = getEntityFromDocument(documentObjet);
            return entity;
        }

        /// <summary>
        /// Extract en Entity stored in the Couchbase document
        /// </summary>
        /// <param name="documentObjet"></param>
        /// <returns></returns>
        private T getEntityFromDocument(IDocument documentObjet)
        {
            return getEntityFromDocument(documentObjet.GetProperty("members"), (string)documentObjet.GetProperty("entityType"));
        }

        private T getEntityFromDocument(object memberField, string originalEntityType)
        {

            T entity = null;

            // Comprendre pourquoi T ou Jobject sont retournés alternativement
            if (memberField is JObject)
            {

                JObject testobject = (JObject)memberField;

                // Determine the destination type to handle polymorphism
                Type destinationType = typeof(T);

                if (!string.IsNullOrEmpty(originalEntityType))
                {
                    // We stored the original entity type
                    Type mappedDestinationType;
                    if (PolymorphicTypes.TryGetValue(originalEntityType, out mappedDestinationType))
                    {
                        // We found a mapped destination type
                        destinationType = mappedDestinationType;

                    }
                }

                entity = (T)testobject.ToObject(destinationType);
            }
            else
            {
                entity = (T)memberField;

            }

            return entity;
        }

        public override T TryGetById(string id)
        {
            // Refactor to optimize this implementation
            try
            {
                return GetById(id);
            }
            catch (KeyNotFoundNoSQLException)
            {
                return null;
            }
        }

        public override bool Exist(string id)
        {
            var documentObjet = this.database.GetDocument(getInternalCBLId(id));
            return documentObjet != null;
        }

        public override BulkInsertResult<string> InsertMany(IEnumerable<T> entities, InsertMode insertMode)
        {
            var insertResult = new BulkInsertResult<string>();

            //TODO : restore Parralel.ForEach
            foreach(var entity in entities)
            {
                // Create the document
                InsertOne(entity, insertMode);
                insertResult[entity.Id] = InsertResult.unknown;
            };
            return insertResult;
        }


        public override InsertResult InsertOne(T entity, InsertMode insertMode)
        {
            var insertResult = default(InsertResult);
            bool documentAlreadyExists = false;
            IDocument documentObjet = null;

            var date = NoSQLRepoHelper.DateTimeUtcNow();
            IDictionary<string, object> properties;

            if (string.IsNullOrEmpty(entity.Id))
            {
                // No id specified, let couchbase generate the id and affect it to the entity
                documentObjet = database.CreateDocument();
                entity.Id = cblGeneratedIdPrefix + documentObjet.Id; // NB: prefix the Id generated by couchbase to be able to distinguish it with a user provided id
            }
            else
            {
                documentObjet = database.GetDocument(getInternalCBLId(entity.Id));

                if (documentObjet.CurrentRevisionId != null)
                {
                    // Document already exists
                    if (insertMode == InsertMode.error_if_key_exists)
                    {
                        throw new DupplicateKeyNoSQLException();
                    }
                    else if (insertMode == InsertMode.do_nothing_if_key_exists)
                    {
                        return InsertResult.not_affected;
                    }

                    documentAlreadyExists = true;
                }
            }

            if (!documentAlreadyExists)
            {
                if (AutoGeneratedEntityDate)
                {
                    entity.SystemCreationDate = date;
                    entity.SystemLastUpdateDate = date;
                }

                properties = new Dictionary<string, object>()
                {
                    {"creat date", date},
                    {"update date", date},
                    {"collection", this.CollectionName},
                    {"members", entity},
                    {"entityType", entity.GetType().Name} // Store the original actual object class to handle polymorphism 
                };

                insertResult = InsertResult.inserted;
            }
            else
            {
                properties = documentObjet.Properties;

                entity.SystemCreationDate = (DateTime)properties["creat date"];
                entity.SystemLastUpdateDate = date;

                properties["update date"] = entity.SystemLastUpdateDate;
                properties["members"] = entity;

                insertResult = InsertResult.updated;
            }

            documentObjet.PutProperties(properties);

            return insertResult;
        }

        public override UpdateResult Update(T entity, UpdateMode updateMode)
        {
            var updateResult = default(UpdateResult);

            if (updateMode == UpdateMode.db_implementation)
            {
                var idDocument = getInternalCBLId(entity.Id);
                var updateDate = NoSQLRepoHelper.DateTimeUtcNow();

                var documentObjet = database.GetDocument(idDocument);

                documentObjet.Update((IUnsavedRevision newRevision) =>
                {
                    var properties = newRevision.Properties;
                    properties["update date"] = updateDate;
                    properties["members"] = entity;
                    return true;
                });
                updateResult = UpdateResult.updated;
            }
            else
            {
                throw new NotImplementedException();
            }
            return updateResult;
        }

        public override void UseDatabase(string dbName)
        {
            throw new NotImplementedException();
        }

        public override long TruncateCollection()
        {
            var query = database.CreateAllDocumentsQuery();
            query.AllDocsMode = QueryAllDocsMode.AllDocs;
            var result = query.Run();
            foreach (var resultItem in result)
            {
                resultItem.Document.Delete();
            }
            return 0;
        }

        public override void DropCollection()
        {
            throw new NotImplementedException();
        }

        public override void SetCollectionName(string typeName)
        {
            this.CollectionName = typeName;
        }

        public override void InitCollection()
        {
            // Nothing to do to initialize the collection
        }

        public override bool CollectionExists(bool createIfNotExists)
        {
            throw new NotImplementedException();
        }

        public override long Delete(string id, bool physical)
        {
            if (!physical)
                throw new NotImplementedException();

            long result = 0;
            var documentObjet = this.database.GetDocument(getInternalCBLId(id));
            // Document found
            if (documentObjet != null && !documentObjet.Deleted)
            {
                documentObjet.Delete();
                result = 1;
            }

            return result;
        }

        #region Attachments
        /// <summary>
        /// Add an attachment to an entity
        /// </summary>
        /// <param name="id">id of entity</param>
        /// <param name="filePathAttachment">file path of the file to attach</param>
        /// <param name="contentType">type of the file to attach</param>
        /// <param name="attachmentName">identify of the file to attach</param>
        public override void AddAttachment(string id, Stream fileStream, string contentType, string attachmentName)
        {
            var existingEntity = this.database.GetDocument(getInternalCBLId(id));
            if (existingEntity == null)
                throw new KeyNotFoundNoSQLException();

            IUnsavedRevision newRevision = existingEntity.CurrentRevision.CreateRevision();
            newRevision.SetAttachment(attachmentName, contentType, fileStream);
            newRevision.Save();
        }

        /// <summary>
        /// Remove the attachment of a document
        /// </summary>
        /// <param name="id">id of entity</param>
        /// <param name="attachmentName">name of attachment to remove</param>
        public override void RemoveAttachment(string id, string attachmentName)
        {
            var existingEntity = this.database.GetDocument(getInternalCBLId(id));
            if (existingEntity == null)
                throw new KeyNotFoundNoSQLException(string.Format("Entity '{0}' not found", id));

            if (!AttachmentExists(existingEntity.CurrentRevision, attachmentName))
                throw new AttachmentNotFoundNoSQLException(string.Format("Attachement {0} not found on Entity '{1}'", attachmentName, id));

            IUnsavedRevision newRevision = existingEntity.CurrentRevision.CreateRevision();
            newRevision.RemoveAttachment(attachmentName);
            newRevision.Save();
        }

        /// <summary>
        /// Get an attachment of an entity or null if the attachement is not found
        /// </summary>
        /// <param name="id">Id of document</param>
        /// <param name="attachmentName">Name of attachment</param>
        /// <returns></returns>
        public override Stream GetAttachment(string id, string attachmentName)
        {
            var attachement = GetAttachmentCore(id, attachmentName);
            if (attachement != null)
                return attachement.ContentStream;
            else
                throw new AttachmentNotFoundNoSQLException();
        }

        public IEnumerable<byte> GetAttachmentInMemory(string id, string attachmentName)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentNullException("id");

            if (string.IsNullOrWhiteSpace(attachmentName))
                throw new ArgumentNullException("attachmentName");

            var attachement = GetAttachmentCore(id, attachmentName);
            if (attachement != null)
                return attachement.Content;
            else
                return null;
        }

        /// <summary>
        /// Indicate if an attachement exits
        /// Should not use "revision.GetAttachment" == null that always returned an object instance, event if the attachment doesn't exist 
        /// </summary>
        /// <param name="revision"></param>
        /// <param name="attachmentName"></param>
        /// <returns></returns>
        private bool AttachmentExists(IRevision revision, string attachmentName)
        {
            return revision.AttachmentNames.Any(a => attachmentName.Equals(a));
        }

        private IAttachment GetAttachmentCore(string id, string attachmentName)
        {
            var documentAttachment = this.database.GetDocument(getInternalCBLId(id));
            if (documentAttachment == null)
                throw new KeyNotFoundNoSQLException();

            var revision = documentAttachment.CurrentRevision;

            if (!AttachmentExists(revision, attachmentName))
                return null;
            else
            {
                var attachment = revision.GetAttachment(attachmentName);
                return attachment;
            }
        }

        #endregion

        public override void InitCollection(List<Expression<Func<T, object>>> indexFieldSelectors)
        {
            throw new NotImplementedException();
        }

        public override IList<T> GetAll()
        {

            IView view = database.GetView(CollectionName);
            using (IQuery query = view.CreateQuery())
            {
                query.Prefetch = true;
                query.AllDocsMode = QueryAllDocsMode.AllDocs;
                query.IndexUpdateMode = IndexUpdateMode.Before;

                using (var queryEnum = query.Run())
                {
                    return queryEnum.Where(row => !row.Document.Deleted)
                        .Select(row => getEntityFromDocument(row.Document)).ToList();
                }
            }
        }

        public override IList<string> GetAttachmentNames(string id)
        {
            var documentAttachment = this.database.GetDocument(getInternalCBLId(id));
            if (documentAttachment == null)
                throw new KeyNotFoundNoSQLException();

            var revision = documentAttachment.CurrentRevision;

            return revision.AttachmentNames.ToList();
        }


        #region Views

        public override List<T> GetByField<TField>(string fieldName, TField value)
        {
            IView view = database.GetExistingView(CollectionName + "-" + fieldName);

            if (view == null)
                throw new IndexNotFoundNoSQLException(string.Format("An index must be created on the fieldName '{0}' before calling GetByField", fieldName));

            using (IQuery query = view.CreateQuery())
            {
                query.Prefetch = true;
                query.StartKey = value;
                query.EndKey = value;
                query.IndexUpdateMode = IndexUpdateMode.Before;

                using (var queryEnum = query.Run())
                {
                    return queryEnum.Where(row => !row.Document.Deleted)
                        .Select(doc => getEntityFromDocument(doc.Document)).ToList();
                }
            }
        }

        public override List<T> GetByField<TField>(string fieldName, List<TField> values)
        {
            return values.SelectMany(v => GetByField(fieldName, v))
                .GroupBy(e => e.Id)
                .Select(g => g.First()) // Remove duplicates entities
                .ToList();
        }

        public override List<string> GetKeyByField<TField>(string fieldName, TField value)
        {
            IView view = database.GetExistingView(CollectionName + "-" + fieldName);

            if (view == null)
                throw new IndexNotFoundNoSQLException(string.Format("An index must be created on the fieldName '{0}' before calling GetByField", fieldName));

            using (IQuery query = view.CreateQuery())
            {
                query.Prefetch = false;
                query.StartKey = value;
                query.EndKey = value;

                using (var queryEnum = query.Run())
                {
                    return queryEnum.Select(doc => getIdFromInternalCBLId(doc.DocumentId)).ToList();
                }
            }
        }

        public override List<string> GetKeyByField<TField>(string fieldName, List<TField> values)
        {
            return values.SelectMany(v => GetKeyByField(fieldName, v)).Distinct().ToList();
        }

        /// <summary>
        /// Create a view to get All document of the collection without scranning the whole database
        /// Reminder : in couchbase, there is no "collection", all objects belong to the same storage
        /// </summary>
        private void CreateAllDocView()
        {
            IView view = database.GetExistingView(CollectionName);

            if (view == null)
            {
                view = database.GetView(CollectionName);
                view.SetMap(
                (doc, emit) =>
                {
                    if (!doc.Keys.Contains("collection") || !doc.Keys.Contains("members"))
                        return; // bad doc format, ignore it

                    var collection = (string)doc["collection"];
                    if (collection == null | !collection.Equals(CollectionName))
                        return; // doc type is not the one of the current collection

                    emit(doc["_id"], doc["_id"]);
                }
            , "1");
            }
            else
            {
                // View already exists, nothing to do
            }
        }

        public void CreateView<TField>(string fieldName, string version)
        {
            var viewName = CollectionName + "-" + fieldName; // view name = collectionName-fieldName
            IView view = database.GetExistingView(viewName);

            if (view == null)
            {
                view = database.GetView(viewName);
                view.SetMap(
                    (doc, emit) =>
                    {
                        if (!doc.Keys.Contains("collection") || !doc.Keys.Contains("members"))
                            return; // bad doc format, ignore it

                        var collection = (string)doc["collection"];
                        if (collection == null | !collection.Equals(CollectionName))
                            return; // doc type is not the one of the current collection

                        JObject jObj = (JObject)doc["members"];

                        string id = jObj.GetValue("Id").Value<string>();

                        JToken jToken = jObj.GetValue(fieldName);
                        if (jToken is JArray)
                        {
                            foreach (var arrayToken in (JArray)jToken)
                            {
                                TField fieldValue = arrayToken.Value<TField>();
                                emit(fieldValue, id);
                            }
                        }
                        else
                        {
                            TField fieldValue = jToken.Value<TField>();
                            emit(fieldValue, id);
                        }
                    }
                , version);

            }
            else
            {
                // View already exists, nothing to do
            }
        }

        #endregion

        #region Private

        /// <summary>
        /// Return the internal Id used in couchbase lite to uniquely identity in the database objects of all collections
        /// Reminder : CBL id's must be unique for all objects (whatever their collection/type)
        /// </summary>
        /// <param name="entityId"></param>
        /// <returns></returns>
        private string getInternalCBLId(string entityId)
        {
            string cblId;
            if (entityId.StartsWith(cblGeneratedIdPrefix))
                // Entity Id has been generated by Couchbase, entityId = couchbaseliteId
                cblId = entityId.Substring(cblGeneratedIdPrefix.Length);
            else
                // Entity Id is user provided, add the suffix to the entityId to ensure Ic
                cblId = string.Concat(CollectionName, "-", entityId);
            return cblId;
        }


        private string getIdFromInternalCBLId(string cblId)
        {
            string id;

            if (cblId.StartsWith(CollectionName + "-"))
                // Entity Id has been generated by Couchbase, entityId = couchbaseliteId
                id = cblId.Substring(CollectionName.Length + 1);
            else
                // Entity Id is user provided, add the suffix to the entityId to ensure Ic
                id = cblGeneratedIdPrefix + cblId;
            return id;
        }


        private const string cblGeneratedIdPrefix = "$$CBL$$";

        #endregion
    }
}
