using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Transactions;
using Abp.Auditing;
using Abp.Dependency;
using Abp.Domain.Entities;
using Abp.Domain.Entities.Auditing;
using Abp.Domain.Uow;
using Abp.Events.Bus.Entities;
using Abp.Extensions;
using Abp.Json;
using Abp.Runtime.Session;
using Abp.Timing;
using Castle.Core.Logging;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Abp.EntityHistory
{
    public class EntityHistoryHelper : IEntityHistoryHelper, ITransientDependency
    {
        public ILogger Logger { get; set; }
        public IAbpSession AbpSession { get; set; }
        public IClientInfoProvider ClientInfoProvider { get; set; }
        public IEntityChangeSetReasonProvider EntityChangeSetReasonProvider { get; set; }
        public IEntityHistoryStore EntityHistoryStore { get; set; }

        private readonly IEntityHistoryConfiguration _configuration;
        private readonly IUnitOfWorkManager _unitOfWorkManager;

        private bool IsEntityHistoryEnabled
        {
            get
            {
                if (!_configuration.IsEnabled)
                {
                    return false;
                }

                if (!_configuration.IsEnabledForAnonymousUsers && (AbpSession?.UserId == null))
                {
                    return false;
                }

                return true;
            }
        }

        public EntityHistoryHelper(
            IEntityHistoryConfiguration configuration,
            IUnitOfWorkManager unitOfWorkManager)
        {
            _configuration = configuration;
            _unitOfWorkManager = unitOfWorkManager;

            AbpSession = NullAbpSession.Instance;
            Logger = NullLogger.Instance;
            ClientInfoProvider = NullClientInfoProvider.Instance;
            EntityChangeSetReasonProvider = NullEntityChangeSetReasonProvider.Instance;
            EntityHistoryStore = NullEntityHistoryStore.Instance;
        }
        
        public virtual EntityChangeSet CreateEntityChangeSet(ICollection<EntityEntry> entityEntries)
        {
            var changeSet = new EntityChangeSet
            {
                Reason = EntityChangeSetReasonProvider.Reason.TruncateWithPostfix(EntityChangeSet.MaxReasonLength),

                // Fill "who did this change"
                BrowserInfo = ClientInfoProvider.BrowserInfo.TruncateWithPostfix(EntityChangeSet.MaxBrowserInfoLength),
                ClientIpAddress = ClientInfoProvider.ClientIpAddress.TruncateWithPostfix(EntityChangeSet.MaxClientIpAddressLength),
                ClientName = ClientInfoProvider.ComputerName.TruncateWithPostfix(EntityChangeSet.MaxClientNameLength),
                ImpersonatorTenantId = AbpSession.ImpersonatorTenantId,
                ImpersonatorUserId = AbpSession.ImpersonatorUserId,
                TenantId = AbpSession.TenantId,
                UserId = AbpSession.UserId
            };

            if (!IsEntityHistoryEnabled)
            {
                return changeSet;
            }

            foreach (var entry in entityEntries)
            {
                if (!ShouldSaveEntityHistory(entry))
                {
                    continue;
                }

                var entityChangeInfo = CreateEntityChangeInfo(entry);
                if (entityChangeInfo == null)
                {
                    continue;
                }

                changeSet.EntityChanges.Add(entityChangeInfo);
            }

            return changeSet;
        }

        public virtual async Task SaveAsync(EntityChangeSet changeSet)
        {
            if (!IsEntityHistoryEnabled)
            {
                return;
            }

            if (changeSet.EntityChanges.Count == 0)
            {
                return;
            }

            UpdateChangeSet(changeSet);

            using (var uow = _unitOfWorkManager.Begin(TransactionScopeOption.Suppress))
            {
                await EntityHistoryStore.SaveAsync(changeSet);
                await uow.CompleteAsync();
            }
        }

        [CanBeNull]
        private EntityChange CreateEntityChangeInfo(EntityEntry entityEntry)
        {
            var entity = entityEntry.Entity;
            
            EntityChangeType changeType;
            switch (entityEntry.State)
            {
                case EntityState.Added:
                    changeType = EntityChangeType.Created;
                    break;
                case EntityState.Deleted:
                    changeType = EntityChangeType.Deleted;
                    break;
                case EntityState.Modified:
                    changeType = IsDeleted(entityEntry) ? EntityChangeType.Deleted : EntityChangeType.Updated;
                    break;
                case EntityState.Detached:
                case EntityState.Unchanged:
                default:
                    Logger.Error("Unexpected EntityState!");
                    return null;
            }

            var entityId = GetEntityId(entity);
            if (entityId == null && changeType != EntityChangeType.Created)
            {
                Logger.Error("Unexpected null value for entityId!");
                return null;
            }

            var entityType = entity.GetType();
            var entityChangeInfo = new EntityChange
            {
                ChangeType = changeType,
                EntityEntry = entityEntry, // [NotMapped]
                EntityId = entityId,
                EntityTypeFullName = entityType.FullName,
                PropertyChanges = GetPropertyChanges(entityEntry)
            };

            return entityChangeInfo;
        }

        private DateTime GetChangeTime(EntityChange entityChange)
        {
            var entity = entityChange.EntityEntry.As<EntityEntry>().Entity;
            switch (entityChange.ChangeType)
            {
                case EntityChangeType.Created:
                    return (entity as IHasCreationTime)?.CreationTime ?? Clock.Now;
                case EntityChangeType.Deleted:
                    return (entity as IHasDeletionTime)?.DeletionTime ?? Clock.Now;
                case EntityChangeType.Updated:
                    return (entity as IHasModificationTime)?.LastModificationTime ?? Clock.Now;
                default:
                    Logger.Error("Unexpected EntityState!");
                    return Clock.Now;
            }
        }

        private string GetEntityId(object entityAsObj)
        {
            return entityAsObj
                .GetType().GetProperty("Id")?
                .GetValue(entityAsObj)?
                .ToJsonString();
        }

        /// <summary>
        /// Gets the property changes for this entry.
        /// </summary>
        private ICollection<EntityPropertyChange> GetPropertyChanges(EntityEntry entityEntry)
        {
            var propertyChanges = new List<EntityPropertyChange>();
            var properties = entityEntry.Metadata.GetProperties();
            var isCreatedOrDeleted = IsCreated(entityEntry) || IsDeleted(entityEntry);

            foreach (var property in properties)
            {
                var propertyEntry = entityEntry.Property(property.Name);
                if (ShouldSavePropertyHistory(propertyEntry, isCreatedOrDeleted))
                {
                    propertyChanges.Add(new EntityPropertyChange
                    {
                        NewValue = propertyEntry.CurrentValue.ToJsonString().TruncateWithPostfix(EntityPropertyChange.MaxValueLength),
                        OriginalValue = propertyEntry.OriginalValue.ToJsonString().TruncateWithPostfix(EntityPropertyChange.MaxValueLength),
                        PropertyName = property.Name,
                        PropertyTypeFullName = property.ClrType.FullName
                    });
                }
            }

            return propertyChanges;
        }

        private bool IsCreated(EntityEntry entityEntry)
        {
            return entityEntry.State == EntityState.Added;
        }

        private bool IsDeleted(EntityEntry entityEntry)
        {
            if (entityEntry.State == EntityState.Deleted)
            {
                return true;
            }

            var entity = entityEntry.Entity;
            return entity is ISoftDelete && entity.As<ISoftDelete>().IsDeleted;
        }

        private bool ShouldSaveEntityHistory(EntityEntry entityEntry, bool defaultValue = false)
        {
            if (entityEntry.State == EntityState.Detached ||
                entityEntry.State == EntityState.Unchanged)
            {
                return false;
            }

            if (_configuration.IgnoredTypes.Any(t => t.IsInstanceOfType(entityEntry.Entity)))
            {
                return false;
            }

            var entityType = entityEntry.Entity.GetType();
            if (!EntityHelper.IsEntity(entityType))
            {
                return false;
            }

            if (!entityType.IsPublic)
            {
                return false;
            }

            if (entityType.GetTypeInfo().IsDefined(typeof(AuditedAttribute), true))
            {
                return true;
            }

            if (entityType.GetTypeInfo().IsDefined(typeof(DisableAuditingAttribute), true))
            {
                return false;
            }

            if (_configuration.Selectors.Any(selector => selector.Predicate(entityType)))
            {
                return true;
            }

            var properties = entityEntry.Metadata.GetProperties();
            if (properties.Any(p => p.PropertyInfo?.IsDefined(typeof(AuditedAttribute)) ?? false))
            {
                return true;
            }

            return defaultValue;
        }

        private bool ShouldSavePropertyHistory(PropertyEntry propertyEntry, bool defaultValue)
        {
            var propertyInfo = propertyEntry.Metadata.PropertyInfo;
            if (propertyInfo.IsDefined(typeof(DisableAuditingAttribute), true))
            {
                return false;
            }

            var classType = propertyInfo.DeclaringType;
            if (classType != null)
            {
                if (classType.GetTypeInfo().IsDefined(typeof(DisableAuditingAttribute), true) &&
                    !propertyInfo.IsDefined(typeof(AuditedAttribute), true))
                {
                    return false;
                }
            }

            var isModified = !(propertyEntry.OriginalValue?.Equals(propertyEntry.CurrentValue) ?? propertyEntry.CurrentValue == null);
            if (isModified)
            {
                return true;
            }

            return defaultValue;
        }

        /// <summary>
        /// Updates change time, entity id and foreign keys after SaveChanges is called.
        /// </summary>
        private void UpdateChangeSet(EntityChangeSet changeSet)
        {
            foreach (var entityChangeInfo in changeSet.EntityChanges)
            {
                /* Update change time */

                entityChangeInfo.ChangeTime = GetChangeTime(entityChangeInfo);

                /* Update entity id */

                var entityEntry = entityChangeInfo.EntityEntry.As<EntityEntry>();
                entityChangeInfo.EntityId = GetEntityId(entityEntry.Entity);

                /* Update foreign keys */

                var foreignKeys = entityEntry.Metadata.GetForeignKeys();

                foreach (var foreignKey in foreignKeys)
                {
                    foreach (var property in foreignKey.Properties)
                    {
                        var propertyEntry = entityEntry.Property(property.Name);
                        var propertyChange = entityChangeInfo.PropertyChanges.FirstOrDefault(pc => pc.PropertyName == property.Name);

                        if (propertyChange == null)
                        {
                            if (!(propertyEntry.OriginalValue?.Equals(propertyEntry.CurrentValue) ?? propertyEntry.CurrentValue == null))
                            {
                                // Add foreign key
                                entityChangeInfo.PropertyChanges.Add(new EntityPropertyChange
                                {
                                    NewValue = propertyEntry.CurrentValue.ToJsonString(),
                                    OriginalValue = propertyEntry.OriginalValue.ToJsonString(),
                                    PropertyName = property.Name,
                                    PropertyTypeFullName = property.ClrType.FullName
                                });
                            }

                            continue;
                        }

                        if (propertyChange.OriginalValue == propertyChange.NewValue)
                        {
                            var newValue = propertyEntry.CurrentValue.ToJsonString();
                            if (newValue == propertyChange.NewValue)
                            {
                                // No change
                                entityChangeInfo.PropertyChanges.Remove(propertyChange);
                            }
                            else
                            {
                                // Update foreign key
                                propertyChange.NewValue = newValue.TruncateWithPostfix(EntityPropertyChange.MaxValueLength);
                            }
                        }
                    }
                }
            }
        }
    }
}
