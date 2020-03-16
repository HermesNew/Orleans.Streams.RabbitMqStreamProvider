﻿using System;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Orleans.Streams.RabbitMq
{
    internal interface IRabbitMqConnectorFactory
    {
        ILoggerFactory LoggerFactory { get; }
        IRabbitMqConsumer CreateConsumer(QueueId queueId);
        IRabbitMqProducer CreateProducer();
        string GetNameForQueue(QueueId queueId);
    }

    internal interface IRabbitMqConsumer : IDisposable
    {
        void Ack(ulong deliveryTag);
        void Nack(ulong deliveryTag);
        BasicGetResult Receive();
    }

    internal interface IRabbitMqProducer : IDisposable
    {
        void Send(string exchange, string routingKey, byte[] message);
    }
}
