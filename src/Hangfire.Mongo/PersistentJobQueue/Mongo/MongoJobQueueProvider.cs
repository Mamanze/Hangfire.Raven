﻿using Hangfire.Mongo.Database;
using System;

namespace Hangfire.Mongo.PersistentJobQueue.Mongo
{
	internal class MongoJobQueueProvider : IPersistentJobQueueProvider
	{
		private readonly MongoStorageOptions _options;

		public MongoJobQueueProvider(MongoStorageOptions options)
		{
			if (options == null)
				throw new ArgumentNullException("options");

			_options = options;
		}

		public IPersistentJobQueue GetJobQueue(HangfireDbContext connection)
		{
			return new MongoJobQueue(connection, _options);
		}

		public IPersistentJobQueueMonitoringApi GetJobQueueMonitoringApi(HangfireDbContext connection)
		{
			return new MongoJobQueueMonitoringApi(connection);
		}
	}
}