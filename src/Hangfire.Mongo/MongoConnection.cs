﻿using System.Linq;
using Hangfire.Common;
using Hangfire.Mongo.Database;
using Hangfire.Mongo.DistributedLock;
using Hangfire.Mongo.Dto;
using Hangfire.Mongo.MongoUtils;
using Hangfire.Mongo.PersistentJobQueue;
using Hangfire.Server;
using Hangfire.Storage;
using MongoDB.Bson;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Threading;
using MongoDB.Driver.Builders;

namespace Hangfire.Mongo
{
	public class MongoConnection : IStorageConnection
	{
		private readonly HangfireDbContext _database;
		private readonly MongoStorageOptions _options;

		private readonly PersistentJobQueueProviderCollection _queueProviders;

		public MongoConnection(HangfireDbContext database, PersistentJobQueueProviderCollection queueProviders)
			: this (database, new MongoStorageOptions(), queueProviders)
		{
		}

		public MongoConnection(HangfireDbContext database, MongoStorageOptions options, PersistentJobQueueProviderCollection queueProviders)
		{
			if (database == null)
				throw new ArgumentNullException("database");

			if (queueProviders == null)
				throw new ArgumentNullException("queueProviders");

			if (options == null)
				throw new ArgumentNullException("options");

			_database = database;
			_options = options;
			_queueProviders = queueProviders;
		}

		public void Dispose()
		{
			_database.Dispose();
		}

		public IWriteOnlyTransaction CreateWriteTransaction()
		{
			return new MongoWriteOnlyTransaction(_database, _queueProviders);
		}

		public IDisposable AcquireDistributedLock(string resource, TimeSpan timeout)
		{
			return new MongoDistributedLock(String.Format("HangFire:{0}", resource), timeout, _database, _options);
		}

		public string CreateExpiredJob(Job job, IDictionary<string, string> parameters, DateTime createdAt, TimeSpan expireIn)
		{
			if (job == null)
				throw new ArgumentNullException("job");

			if (parameters == null)
				throw new ArgumentNullException("parameters");

			var invocationData = InvocationData.Serialize(job);

			var jobDto = new JobDto
			{
				InvocationData = JobHelper.ToJson(invocationData),
				Arguments = invocationData.Arguments,
				CreatedAt = createdAt,
				ExpireAt = createdAt.Add(expireIn)
			};

			_database.Job.Insert(jobDto);

			var jobId = jobDto.Id;

			if (parameters.Count > 0)
			{
				foreach (var parameter in parameters)
				{
					_database.JobParameter.Insert(new JobParameterDto
					{
						JobId = jobId,
						Name = parameter.Key,
						Value = parameter.Value
					});
				}
			}

			return jobId.ToString();
		}

		public IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
		{
			if (queues == null || queues.Length == 0)
				throw new ArgumentNullException("queues");

			var providers = queues
				.Select(queue => _queueProviders.GetProvider(queue))
				.Distinct()
				.ToArray();

			if (providers.Length != 1)
			{
				throw new InvalidOperationException(String.Format("Multiple provider instances registered for queues: {0}. You should choose only one type of persistent queues per server instance.",
					String.Join(", ", queues)));
			}

			var persistentQueue = providers[0].GetJobQueue(_database);
			return persistentQueue.Dequeue(queues, cancellationToken);
		}

		public void SetJobParameter(string id, string name, string value)
		{
			if (id == null)
				throw new ArgumentNullException("id");

			if (name == null)
				throw new ArgumentNullException("name");

			_database.JobParameter.Update(Query.And(Query<JobParameterDto>.EQ(_ => _.JobId, int.Parse(id)), Query<JobParameterDto>.EQ(_ => _.Name, name)),
				Update<JobParameterDto>.Set(_ => _.Value, value),
				UpdateFlags.Upsert);
		}

		public string GetJobParameter(string id, string name)
		{
			if (id == null)
				throw new ArgumentNullException("id");

			if (name == null)
				throw new ArgumentNullException("name");

			var jobParameter = _database.JobParameter.FindOne(Query.And(Query<JobParameterDto>.EQ(_ => _.JobId, int.Parse(id)),
				Query<JobParameterDto>.EQ(_ => _.Name, name)));

			return jobParameter != null ? jobParameter.Value : null;
		}

		public JobData GetJobData(string jobId)
		{
			if (jobId == null)
				throw new ArgumentNullException("jobId");

			var jobData = _database.Job.FindOneById(int.Parse(jobId));

			if (jobData == null)
				return null;

			// TODO: conversion exception could be thrown.
			var invocationData = JobHelper.FromJson<InvocationData>(jobData.InvocationData);
			invocationData.Arguments = jobData.Arguments;

			Job job = null;
			JobLoadException loadException = null;

			try
			{
				job = invocationData.Deserialize();
			}
			catch (JobLoadException ex)
			{
				loadException = ex;
			}

			return new JobData
			{
				Job = job,
				State = jobData.StateName,
				CreatedAt = jobData.CreatedAt,
				LoadException = loadException
			};
		}

		public StateData GetStateData(string jobId)
		{
			if (jobId == null)
				throw new ArgumentNullException("jobId");

			JobDto job = _database.Job.FindOneById(int.Parse(jobId));
			if (job == null)
				return null;

			StateDto state = _database.State.FindOne(Query<StateDto>.EQ(_ => _.Id, job.StateId));
			if (state == null)
				return null;

			return new StateData
			{
				Name = state.Name,
				Reason = state.Reason,
				Data = JobHelper.FromJson<Dictionary<string, string>>(state.Data)
			};
		}

		public void AnnounceServer(string serverId, ServerContext context)
		{
			if (serverId == null)
				throw new ArgumentNullException("serverId");

			if (context == null)
				throw new ArgumentNullException("context");

			var data = new ServerDataDto
			{
				WorkerCount = context.WorkerCount,
				Queues = context.Queues,
				StartedAt = _database.GetServerTimeUtc(),
			};

			_database.Server.Update(Query<ServerDto>.EQ(_ => _.Id, serverId),
				Update.Combine(Update<ServerDto>.Set(_ => _.Data, JobHelper.ToJson(data)), Update<ServerDto>.Set(_ => _.LastHeartbeat, _database.GetServerTimeUtc())),
				UpdateFlags.Upsert);
		}

		public void RemoveServer(string serverId)
		{
			if (serverId == null)
				throw new ArgumentNullException("serverId");

			_database.Server.Remove(Query<ServerDto>.EQ(_ => _.Id, serverId));
		}

		public void Heartbeat(string serverId)
		{
			if (serverId == null)
				throw new ArgumentNullException("serverId");

			_database.Server.Update(Query<ServerDto>.EQ(_ => _.Id, serverId),
				Update<ServerDto>.Set(_ => _.LastHeartbeat, _database.GetServerTimeUtc()));
		}

		public int RemoveTimedOutServers(TimeSpan timeOut)
		{
			if (timeOut.Duration() != timeOut)
				throw new ArgumentException("The `timeOut` value must be positive.", "timeOut");

			return (int)_database.Server.Remove(Query<ServerDto>.LT(_ => _.LastHeartbeat, _database.GetServerTimeUtc().Add(timeOut.Negate()))).DocumentsAffected;
		}

		public HashSet<string> GetAllItemsFromSet(string key)
		{
			if (key == null) throw new ArgumentNullException("key");

			var result = _database.Set.Find(Query<SetDto>.EQ(_ => _.Key, key)).Select(x => x.Value);

			return new HashSet<string>(result);
		}

		public string GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore)
		{
			if (key == null)
				throw new ArgumentNullException("key");

			if (toScore < fromScore)
				throw new ArgumentException("The `toScore` value must be higher or equal to the `fromScore` value.");

			var set = _database.Set
				.Find(Query.And(Query<SetDto>.EQ(_ => _.Key, key),
					Query.And(Query<SetDto>.GTE(_ => _.Score, fromScore), Query<SetDto>.LTE(_ => _.Score, toScore))))
				.SetSortOrder(SortBy<SetDto>.Ascending(_ => _.Score))
				.SetLimit(1)
				.FirstOrDefault();

			return set != null ? set.Value : null;
		}

		public void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
		{
			if (key == null)
				throw new ArgumentNullException("key");

			if (keyValuePairs == null)
				throw new ArgumentNullException("keyValuePairs");

			foreach (var keyValuePair in keyValuePairs)
			{
				_database.Hash.Update(Query.And(Query<HashDto>.EQ(_ => _.Key, key), Query<HashDto>.EQ(_ => _.Field, keyValuePair.Key)),
					Update<HashDto>.Set(_ => _.Value, keyValuePair.Value),
					UpdateFlags.Upsert);
			}
		}

		public Dictionary<string, string> GetAllEntriesFromHash(string key)
		{
			if (key == null)
				throw new ArgumentNullException("key");

			var result = _database.Hash.Find(Query<HashDto>.EQ(_ => _.Key, key))
				.ToDictionary(x => x.Field, x => x.Value);

			return result.Count != 0 ? result : null;
		}
	}
}
