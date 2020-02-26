﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Streams.RabbitMq;

namespace Orleans.Streams
{
    internal class RabbitMqAdapterReceiver : IQueueAdapterReceiver
    {
        private readonly IRabbitMqConnectorFactory _rmqConnectorFactory;
        private readonly QueueId _queueId;
        private readonly IQueueDataAdapter<RabbitMqMessage, IBatchContainer> _dataAdapter;
        private readonly TimeSpan _cacheFillingTimeout;
        private readonly ILogger _logger;
        private long _sequenceId;
        private IRabbitMqConsumer _consumer;
        private readonly List<PendingDelivery> pending;

        public RabbitMqAdapterReceiver(IRabbitMqConnectorFactory rmqConnectorFactory, QueueId queueId, IQueueDataAdapter<RabbitMqMessage, IBatchContainer> dataAdapter, TimeSpan cacheFillingTimeout)
        {
            _rmqConnectorFactory = rmqConnectorFactory;
            _queueId = queueId;
            _dataAdapter = dataAdapter;
            _cacheFillingTimeout = cacheFillingTimeout;
            _logger = _rmqConnectorFactory.LoggerFactory.CreateLogger($"{typeof(RabbitMqAdapterReceiver).FullName}.{queueId}");
            _sequenceId = 0;
            pending = new List<PendingDelivery>();
        }

        public Task Initialize(TimeSpan timeout)
        {
            _consumer = _rmqConnectorFactory.CreateConsumer(_queueId);
            return Task.CompletedTask;
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        {
            var multibatch = new List<IBatchContainer>();
            var startTimestamp = DateTime.UtcNow;
            for (int count = 0; count < maxCount || maxCount == QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG; count++)
            {
                // in case of high latency on the network, new messages will be coming in a low rate and until the cache
                // will be filled, we would will be looping here; the timeout is here to break the loop and start the
                // consumption if it takes too long to fill the cache
                if (DateTime.UtcNow - startTimestamp > _cacheFillingTimeout) break;

                // on a very slow network with high latency, the synchronous RMQ Receive will block all worker threads until
                // the RMQ queue is empty or the cache is full; in order to enforce the consumption, the Yield is called,
                // which foces asynchronicity and allows other scheduled methods (the consumers) to continue;
                // the right ways would be to await a ReceiveAsync, but there is currently no such method in RMQ client library;
                // we could only wrap the call in Task.Run, which is also a bad practice
                await Task.Yield();

                var item = _consumer.Receive();
                if (item == null) break;
                try
                {
                    var batch = _dataAdapter.FromQueueMessage(item, _sequenceId++);
                    multibatch.Add(batch);
                    pending.Add(new PendingDelivery(batch.SequenceToken, item.DeliveryTag, item.Channel));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "GetQueueMessagesAsync: failed to deserialize the message! The message will be thrown away (by calling ACK).");
                    _consumer.Ack(item.Channel, item.DeliveryTag, multiple: false);
                }
            }
            return multibatch;
        }

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            if (!messages.Any()) return Task.CompletedTask;

            List<StreamSequenceToken> deliveredTokens = messages.Select(message => message.SequenceToken).ToList();

            StreamSequenceToken newest = deliveredTokens.Max();

            newest = HandlePartiallyProcessedGroup(deliveredTokens, newest);
            if(newest == null)
            {
                return Task.CompletedTask;
            }

            // finalize all pending messages at or before the newest
            List<PendingDelivery> finalizedDeliveries = pending
                .Where(pendingDelivery => !pendingDelivery.Token.Newer(newest))
                .ToList();

            // remove all finalized deliveries from pending, regardless of if it was delivered or not.
            pending.RemoveRange(0, finalizedDeliveries.Count);

            var groups = finalizedDeliveries.GroupBy(x => new { x.Channel, x.DeliveryTag });

            var groupsByDeliveryStatus = groups.ToLookup(g => g.All(m => deliveredTokens.Contains(m.Token)), g => g.Key);

            var incompletelyDeliveredGroups = groupsByDeliveryStatus[false];

            // Nack any message groups that were not completely delivered
            foreach (var group in incompletelyDeliveredGroups)
            {
                if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug($"MessagesDeliveredAsync NACK #{group.DeliveryTag}");
                _consumer.Nack(group.Channel, group.DeliveryTag);
            }

            var fullyDeliveredGroups = groupsByDeliveryStatus[true];

            // Ack all the rest
            var maxTagsByChannel = fullyDeliveredGroups
                .GroupBy(m => m.Channel)
                .Select(g => new { Channel = g.Key, DeliveryTag = g.Max(m => m.DeliveryTag) });

            foreach (var maxTag in maxTagsByChannel)
            {
                if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug($"MessagesDeliveredAsync ACK #{maxTag.DeliveryTag}");
                _consumer.Ack(maxTag.Channel, maxTag.DeliveryTag, multiple: true);
            }

            return Task.CompletedTask;
        }

        private StreamSequenceToken HandlePartiallyProcessedGroup(List<StreamSequenceToken> deliveredTokens, StreamSequenceToken newest)
        {
            // If newest is part of a group of batches that came from a single rabbit message and not all of them have tokens <= newest,
            // then adjust newest to be largest value not part of that group.
            PendingDelivery top = pending.First(m => m.Token == newest);
            List<PendingDelivery> topGroup = pending.Where(m => m.Channel == top.Channel && m.DeliveryTag == top.DeliveryTag).ToList();
            if (topGroup.Any(x => x.Token.Newer(newest)))
            {
                var remainder = pending.Where(x => !x.Token.Newer(newest)).Where(x => !topGroup.Contains(x)).ToList();
                if (!remainder.Any())
                {
                    // If topGroup is the only group with tokens <= newest, remove any delivered messages from
                    // pending, and return early. (We need to keep any unsuccessfully delivered messages so that we can
                    // Nack the group once it is finished).
                    var delivered = topGroup.Where(msg => deliveredTokens.Contains(msg.Token)).ToList();
                    pending.RemoveAll(delivered.Contains);
                    return null;
                }
                newest = topGroup.Max(x => x.Token);
            }
            return newest;
        }

        public Task Shutdown(TimeSpan timeout)
        {
            _consumer.Dispose();
            return Task.CompletedTask;
        }

        private class PendingDelivery
        {
            public PendingDelivery(StreamSequenceToken token, ulong deliveryTag, object channel)
            {
                this.Token = token;
                this.DeliveryTag = deliveryTag;
                this.Channel = channel;
            }

            public ulong DeliveryTag { get; }
            public object Channel { get; }
            public StreamSequenceToken Token { get; }
        }
    }
}