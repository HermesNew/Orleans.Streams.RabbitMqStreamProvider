﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streams.BatchContainer;
using Orleans.Streams.RabbitMq;

namespace Orleans.Streams
{
    /// <summary>
    /// For RMQ client, it is necessary the Model (channel) is not accessed by multiple threads at once, because with each such access,
    /// the channel gets closed - this is a limitation of RMQ client, which unfortunately causes message loss.
    /// Here we handle it by creating new connection for each receiver which guarantees no overlapping calls from different threads.
    /// The real issue comes in publishing - here we need to identify the connections by thread!
    /// Otherwise it would cause a lot of trouble when publishing messages from StatelessWorkers which can run in parallel, thus
    /// overlapping calls from different threads would occur frequently.
    /// </summary>
    internal class RabbitMqAdapter : IQueueAdapter
    {
        private readonly IBatchContainerSerializer _serializer;
        private readonly IStreamQueueMapper _mapper;
        private readonly ThreadLocal<IRabbitMqProducer> _producer;
        private readonly IRabbitMqConnectorFactory _rmqConnectorFactory;
        private readonly TimeSpan _cacheFillingTimeout;

        public RabbitMqAdapter(RabbitMqOptions rmqOptions, CachingOptions cachingOptions, IBatchContainerSerializer serializer, IStreamQueueMapper mapper, string providerName, ILoggerFactory loggerFactory)
        {
            _serializer = serializer;
            _mapper = mapper;
            Name = providerName;
            _rmqConnectorFactory = new RabbitMqOnlineConnectorFactory(rmqOptions, loggerFactory);
            _cacheFillingTimeout = cachingOptions.CacheFillingTimeout;
            _producer = new ThreadLocal<IRabbitMqProducer>(() => _rmqConnectorFactory.CreateProducer());
        }

        public string Name { get; }
        public bool IsRewindable => false;
        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;
        public IQueueAdapterReceiver CreateReceiver(QueueId queueId) => new RabbitMqAdapterReceiver(_rmqConnectorFactory, queueId, _serializer, _cacheFillingTimeout);

        public async Task QueueMessageBatchAsync<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            if (token != null) throw new ArgumentException("RabbitMq stream provider does not support non-null StreamSequenceToken.", nameof(token));

            var queueId = _mapper.GetQueueForStream(streamGuid, streamNamespace);

            await _producer.Value.SendAsync(string.Empty, _rmqConnectorFactory.GetNameForQueue(queueId), RabbitMqDataAdapter.ToQueueMessage(_serializer, streamGuid, streamNamespace, events, requestContext), shouldConfirm: true, persistent: true );
        }
    }
}