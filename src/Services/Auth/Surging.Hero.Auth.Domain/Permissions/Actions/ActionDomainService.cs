﻿using Dapper;
using Surging.Hero.Common;
using Surging.Core.AutoMapper;
using Surging.Core.CPlatform.Utilities;
using Surging.Core.Dapper.Manager;
using Surging.Core.Dapper.Repositories;
using Surging.Hero.Auth.Domain.Permissions.Operations;
using Surging.Hero.Auth.IApplication.Action.Dtos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq.Expressions;
using Nest;
using Surging.Core.Lock.Provider;
using Surging.Core.Lock;

namespace Surging.Hero.Auth.Domain.Permissions.Actions
{
    public class ActionDomainService : ManagerBase, IActionDomainService
    {
        private readonly IDapperRepository<Action, long> _actionRepository;
        private readonly IDapperRepository<OperationActionRelation, long> _operationActionRelationRepository;
        private readonly ILockerProvider _lockerProvider;

        public ActionDomainService(IDapperRepository<Action, long> actionRepository,
            IDapperRepository<OperationActionRelation, long> operationActionRelationRepository,
            ILockerProvider lockProvider)
        {
            _actionRepository = actionRepository;
            _operationActionRelationRepository = operationActionRelationRepository;
            _lockerProvider = lockProvider;
        }

        public async Task<IEnumerable<GetAppServiceOutput>> GetAppServices(QueryAppServiceInput query)
        {
            var sql = $"SELECT Application as AppService, '{query.ServiceHost}' as ServiceHost FROM `Action` WHERE ServiceHost='{query.ServiceHost}' GROUP BY Application";
            var sqlParams = new Dictionary<string, object>();
            if (!query.AppService.IsNullOrWhiteSpace())
            {
                sql += " HAVING Application LIKE @Application";
                sqlParams.Add("Application", $"%{query.AppService}%");
            }
         
            using (Connection)
            {
                var queryResult = await Connection.QueryAsync<GetAppServiceOutput>(sql, sqlParams);
                return queryResult;
            }
        }

        public async Task<IEnumerable<Action>> GetOperationOutputActions(long id)
        {
            var sql = "SELECT a.* FROM OperationActionRelation as oar LEFT JOIN Action as a on oar.ActionId = a.Id  WHERE oar.OperationId = @OperationId";
            using (Connection) {
                return await Connection.QueryAsync<Action>(sql, new { OperationId = id });
            }
        }

        public async Task<IEnumerable<GetServiceHostOutput>> GetServiceHosts(QueryServiceHostInput query)
        {
            var sql = "SELECT ServiceHost FROM `Action` GROUP BY ServiceHost";
            var sqlParams = new Dictionary<string, object>();
            if (query != null && !query.ServiceHost.IsNullOrWhiteSpace()) 
            {
                sql += " HAVING ServiceHost LIKE @ServiceHost";
                sqlParams.Add("ServiceHost", $"%{query.ServiceHost}%");
            }
            using (Connection) 
            {
                var queryResult = await Connection.QueryAsync<GetServiceHostOutput>(sql, sqlParams);
                return queryResult;
            }
        }

        public async Task<IEnumerable<GetActionOutput>> GetActionServices(QueryActionInput query)
        {
            var sqlParams = new Dictionary<string, object>();
            var sql = "SELECT * FROM  Action  WHERE EnableAuthorization=@EnableAuthorization AND AllowPermission=@AllowPermission";

            sqlParams.Add("EnableAuthorization", 1);
            sqlParams.Add("AllowPermission", 0);
            if (!query.ServiceHost.IsNullOrEmpty()) 
            {
                sql += " AND ServiceHost=@ServiceHost";
                sqlParams.Add("ServiceHost", query.ServiceHost);
            }
            if (!query.AppService.IsNullOrEmpty())
            {
                sql += " AND Application=@AppService";
                sqlParams.Add("AppService", query.AppService);
            }
            if (!query.Service.IsNullOrEmpty())
            {
                sql += " AND (ServiceId=@Service OR Name=@Service)";
                sqlParams.Add("Service", query.Service);
            }
            if (query.Ids != null)
            {
                sql += " AND Id IN @Ids";
                sqlParams.Add("Ids", query.Ids);
            }
            using (Connection) 
            {
                var serviceActions = await Connection.QueryAsync<Action>(sql,sqlParams);
                return serviceActions.MapTo<IEnumerable<GetActionOutput>>();
            }

        }

        public async Task<IEnumerable<GetTreeActionOutput>> GetServicesTree()
        {
            var result = new List<GetTreeActionOutput>();
            var hosts = await GetServiceHosts(null);
            if (hosts != null && hosts.Any())
            {
                var hostIndex = 1;
                var appServiceIndex = 2;
                foreach (var host in hosts) 
                {
                    var hostOutput = new GetTreeActionOutput() { Label = host.ServiceHost, Value = hostIndex };
                    var application = await GetAppServices(new QueryAppServiceInput() { ServiceHost = host.ServiceHost });                    
                    hostOutput.Children = application.Select(p => 
                    {                       
                        var appServiceOutput = new GetTreeActionOutput() { Label = p.AppService, Value = appServiceIndex, Children = GetLeafActions(p.ServiceHost, p.AppService).Result };
                        return appServiceOutput;
                    });
                    result.Add(hostOutput);
                }
            }
            return result;
        }

        private async Task<IEnumerable<GetTreeActionOutput>> GetLeafActions(string serviceHost, string appService)
        {
            var actionServices = await GetActionServices(new QueryActionInput() { ServiceHost = serviceHost, AppService = appService });
            return actionServices.Select(p => new GetTreeActionOutput() { Label = !p.Name.IsNullOrEmpty() ? p.Name : p.ServiceId, Value = p.Id });
        }

        public async Task InitActions(ICollection<InitActionActionInput> actions)
        {
            using (var locker = await _lockerProvider.CreateLockAsync("InitActions")) 
            {
                foreach (var action in actions) 
                {
                    await locker.Lock(async () => 
                    {
                        await UnitOfWorkAsync(async (conn, trans) =>
                        {
                            var actionEntity = await _actionRepository.SingleOrDefaultAsync(p => p.ServiceId == action.ServiceId);
                            if (actionEntity == null)
                            {
                                actionEntity = action.MapTo<Action>();
                                var actionId = await _actionRepository.InsertAndGetIdAsync(actionEntity, conn, trans);
                                var operationActionRelations = await _operationActionRelationRepository.GetAllAsync(p => p.ServiceId == action.ServiceId, conn, trans);
                                if (operationActionRelations.Any())
                                {
                                    foreach (var operationActionRelation in operationActionRelations)
                                    {
                                        operationActionRelation.ActionId = actionId;
                                        await _operationActionRelationRepository.UpdateAsync(operationActionRelation, conn, trans);
                                    }
                                }
                            }
                            else
                            {
                                actionEntity = action.MapTo(actionEntity);
                                await _actionRepository.UpdateAsync(actionEntity, conn, trans);
                                var operationActionRelations = await _operationActionRelationRepository.GetAllAsync(p => p.ServiceId == action.ServiceId);
                                if (operationActionRelations.Any())
                                {
                                    foreach (var operationActionRelation in operationActionRelations)
                                    {
                                        if (operationActionRelation.ActionId != actionEntity.Id)
                                        {
                                            operationActionRelation.ActionId = actionEntity.Id;
                                            await _operationActionRelationRepository.UpdateAsync(operationActionRelation, conn, trans);
                                        }

                                    }
                                }
                            }
                        }, Connection);
                    });
                }
            }

        }
    }
}
