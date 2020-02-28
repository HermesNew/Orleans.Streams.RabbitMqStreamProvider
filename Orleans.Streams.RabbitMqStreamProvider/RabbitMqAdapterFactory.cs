﻿using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams.BatchContainer;

namespace Orleans.Streams
{
    public class RabbitMqAdapterFactory : IQueueAdapterFactory
    {
        private readonly IQueueAdapterCache _cache;
        private readonly IStreamQueueMapper _mapper;
        private readonly Task<IStreamFailureHandler> _failureHandler;
        private readonly IQueueAdapter _adapter;
        
        public RabbitMqAdapterFactory(
            string providerName,
            RabbitMqOptions rmqOptions,
            CachingOptions cachingOptions,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            IRabbitMqStreamQueueMapperFactory streamQueueMapperFactory)
        {
            if (string.IsNullOrEmpty(providerName)) throw new ArgumentNullException(nameof(providerName));
            if (rmqOptions == null) throw new ArgumentNullException(nameof(rmqOptions));
            if (cachingOptions == null) throw new ArgumentNullException(nameof(cachingOptions));
            if (serviceProvider == null) throw new ArgumentNullException(nameof(serviceProvider));
            if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));

            _cache = new SimpleQueueAdapterCache(new SimpleQueueCacheOptions() { CacheSize = cachingOptions.CacheSize}, providerName, loggerFactory);
            _mapper = streamQueueMapperFactory.Get(providerName);
            _failureHandler = Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler(false));

            var serializer = serviceProvider.GetServiceByName<IBatchContainerSerializer>(providerName) ??
                new DefaultBatchContainerSerializer(serviceProvider.GetRequiredService<SerializationManager>());

            _adapter = new RabbitMqAdapter(rmqOptions, cachingOptions, serializer, _mapper, providerName, loggerFactory);
        }

        public Task<IQueueAdapter> CreateAdapter() => Task.FromResult(_adapter);
        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) => _failureHandler;
        public IQueueAdapterCache GetQueueAdapterCache() => _cache;
        public IStreamQueueMapper GetStreamQueueMapper() => _mapper;

        public static RabbitMqAdapterFactory Create(IServiceProvider services, string name)
            => ActivatorUtilities.CreateInstance<RabbitMqAdapterFactory>(
                services,
                name,
                services.GetOptionsByName<RabbitMqOptions>(name),
                services.GetOptionsByName<CachingOptions>(name));
    }
}