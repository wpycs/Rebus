﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Rebus.Bus;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Subscriptions;
using Rebus.Transport;
using RabbitMQ.Client.Events;
using Rebus.Exceptions;
using Headers = Rebus.Messages.Headers;

#pragma warning disable 1998

namespace Rebus.RabbitMq
{
    /// <summary>
    /// Implementation of <see cref="ITransport"/> that uses RabbitMQ to send/receive messages
    /// </summary>
    public class RabbitMqTransport : ITransport, IDisposable, IInitializable, ISubscriptionStorage
    {
        const string CurrentModelItemsKey = "rabbitmq-current-model";
        const string OutgoingMessagesItemsKey = "rabbitmq-outgoing-messages";
        const int TwoSeconds = 2000;

        static readonly Encoding HeaderValueEncoding = Encoding.UTF8;

        readonly ConcurrentDictionary<string, bool> _initializedQueues = new ConcurrentDictionary<string, bool>();
        readonly ConnectionManager _connectionManager;
        readonly ILog _log;

        readonly object _consumerInitializationLock = new object();

        QueueingBasicConsumer _consumer;
        ushort _maxMessagesToPrefetch;

        /// <summary>
        /// Constructs the transport with a connection to the RabbitMQ instance specified by the given connection string
        /// </summary>
        public RabbitMqTransport(string connectionString, string inputQueueAddress, IRebusLoggerFactory rebusLoggerFactory, ushort maxMessagesToPrefetch = 50)
        {
            _connectionManager = new ConnectionManager(connectionString, inputQueueAddress, rebusLoggerFactory);
            _maxMessagesToPrefetch = maxMessagesToPrefetch;
            _log = rebusLoggerFactory.GetCurrentClassLogger();
            Address = inputQueueAddress;
        }

        bool _declareExchanges = true;
        bool _declareInputQueue = true;
        bool _bindInputQueue = true;

        string _directExchangeName = RabbitMqOptionsBuilder.DefaultDirectExchangeName;
        string _topicExchangeName = RabbitMqOptionsBuilder.DefaultTopicExchangeName;

        /// <summary>
        /// Stores the client properties to be haded to RabbitMQ when the connection is established
        /// </summary>
        public void AddClientProperties(Dictionary<string, string> additionalClientProperties)
        {
            _connectionManager.AddClientProperties(additionalClientProperties);
        }

        public void SetDeclareExchanges(bool value)
        {
            _declareExchanges = value;
        }

        public void SetDeclareInputQueue(bool value)
        {
            _declareInputQueue = value;
        }

        public void SetBindInputQueue(bool value)
        {
            _bindInputQueue = value;
        }

        public void SetDirectExchangeName(string directExchangeName)
        {
            _directExchangeName = directExchangeName;
        }

        public void SetTopicExchangeName(string topicExchangeName)
        {
            _topicExchangeName = topicExchangeName;
        }

        public void SetMaxMessagesToPrefetch(int maxMessagesToPrefetch)
        {
            if (maxMessagesToPrefetch <= 0)
            {
                throw new ArgumentException($"Cannot set 'max messages to prefetch' to {maxMessagesToPrefetch} - it must be at least 1!");
            }
            _maxMessagesToPrefetch = (ushort)maxMessagesToPrefetch;
        }

        public void Initialize()
        {
            if (Address == null) { return; }

            CreateQueue(Address);
        }

        public void CreateQueue(string address)
        {
            var connection = _connectionManager.GetConnection();

            using (var model = connection.CreateModel())
            {
                const bool durable = true;

                if (_declareExchanges)
                {
                    model.ExchangeDeclare(_directExchangeName, ExchangeType.Direct, durable);
                    model.ExchangeDeclare(_topicExchangeName, ExchangeType.Topic, durable);
                }

                if (_declareInputQueue)
                {
                    DeclareQueue(address, model, durable);
                }

                if (_bindInputQueue)
                {
                    BindInputQueue(address, model);
                }
            }
        }

        void BindInputQueue(string address, IModel model)
        {
            model.QueueBind(address, _directExchangeName, address);
        }

        static void DeclareQueue(string address, IModel model, bool durable)
        {
            var arguments = new Dictionary<string, object>
            {
                {"x-ha-policy", "all"}
            };

            model.QueueDeclare(address,
                exclusive: false,
                durable: durable,
                autoDelete: false,
                arguments: arguments);
        }

        public async Task Send(string destinationAddress, TransportMessage message, ITransactionContext context)
        {
            var outgoingMessages = context.GetOrAdd(OutgoingMessagesItemsKey, () =>
            {
                var messages = new ConcurrentQueue<OutgoingMessage>();

                context.OnCommitted(() => SendOutgoingMessages(context, messages));

                return messages;
            });

            outgoingMessages.Enqueue(new OutgoingMessage(destinationAddress, message));
        }

        public string Address { get; }

        /// <summary>
        /// Deletes all messages from the queue
        /// </summary>
        public void PurgeInputQueue()
        {
            var connection = _connectionManager.GetConnection();

            using (var model = connection.CreateModel())
            {
                try
                {
                    model.QueuePurge(Address);
                }
                catch (OperationInterruptedException exception)
                {
                    var shutdownReason = exception.ShutdownReason;

                    var queueDoesNotExist = shutdownReason != null
                                            && shutdownReason.ReplyCode == 404;

                    if (queueDoesNotExist)
                    {
                        return;
                    }

                    throw;
                }
            }
        }

        public async Task<TransportMessage> Receive(ITransactionContext context, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (Address == null)
            {
                throw new InvalidOperationException("This RabbitMQ transport does not have an input queue, hence it is not possible to reveive anything");
            }

            try
            {
                EnsureConsumerInitialized();

                var consumer = _consumer;

                // initialization must have failed
                if (consumer == null) return null;

                var model = consumer.Model;

                if (!model.IsOpen)
                {
                    // something is wrong - we would not be able to ACK messages - force re-initialization to happen
                    _consumer = null;

                    // try to get rid of the consumer we have here
                    try
                    {
                        model.Dispose();
                    }
                    catch { }
                }

                BasicDeliverEventArgs result;
                if (!consumer.Queue.Dequeue(TwoSeconds, out result)) return null;

                var deliveryTag = result.DeliveryTag;

                context.OnCommitted(async () =>
                {
                    model.BasicAck(deliveryTag, false);
                });

                context.OnAborted(() =>
                {
                    // we might not be able to do this, but it doesn't matter that much if it succeeds
                    try
                    {
                        model.BasicNack(deliveryTag, false, true);
                    }
                    catch { }
                });

                return CreateTransportMessage(result);
            }
            catch (EndOfStreamException exception)
            {
                ClearConsumer();

                throw new RebusApplicationException(exception,
                    "Queue throw EndOfStreamException(meaning it was canceled by rabbitmq)");
            }
            catch (Exception exception)
            {
                ClearConsumer();

                Thread.Sleep(1000);

                throw new RebusApplicationException(exception,
                    $"unexpected exception thrown while trying to dequeue a message from rabbitmq, queue address: {Address}");
            }
        }

        void EnsureConsumerInitialized()
        {
            // if the consumer is null at this point, we see if we can initialize it...
            if (_consumer == null)
            {
                lock (_consumerInitializationLock)
                {
                    // if there is a consumer instance at this point, someone else went and initialized it...
                    if (_consumer == null)
                    {
                        try
                        {
                            _consumer = InitializeConsumer();
                        }
                        catch
                        {
                            Thread.Sleep(2000);
                        }
                    }
                }
            }
        }

        void ClearConsumer()
        {
            var currentConsumer = _consumer;

            _consumer = null;

            if (currentConsumer == null) return;

            try
            {
                currentConsumer.Model.Dispose();
            }
            catch { }
        }

        /// <summary>
        /// Creates the transport message.
        /// </summary>
        /// <param name="result">The <see cref="BasicDeliverEventArgs"/> instance containing the event data.</param>
        /// <returns>the TransportMessage</returns>
        static TransportMessage CreateTransportMessage(BasicDeliverEventArgs result)
        {
            var headers = result.BasicProperties.Headers
                   .ToDictionary(kvp => kvp.Key, kvp =>
                   {
                       var headerValue = kvp.Value;

                       if (headerValue is byte[])
                       {
                           var stringHeaderValue = HeaderValueEncoding.GetString((byte[])headerValue);

                           return stringHeaderValue;
                       }

                       return headerValue.ToString();
                   });

            return new TransportMessage(headers, result.Body);
        }

        /// <summary>
        /// Creates the consumer.
        /// </summary>
        QueueingBasicConsumer InitializeConsumer()
        {
            IConnection connection = null;
            IModel model = null;
            try
            {
                // receive must be done with separate model
                connection = _connectionManager.GetConnection();
                model = connection.CreateModel();
                model.BasicQos(0, _maxMessagesToPrefetch, false);
                var consumer = new QueueingBasicConsumer(model);

                model.BasicConsume(Address, false, consumer);

                _log.Info("Successfully initialized consumer for {0}", Address);

                return consumer;
            }
            catch (Exception)
            {
                try
                {
                    model?.Dispose();
                }
                catch { }

                try
                {
                    connection?.Dispose();
                }
                catch { }

                throw;
            }
        }


        async Task SendOutgoingMessages(ITransactionContext context, ConcurrentQueue<OutgoingMessage> outgoingMessages)
        {
            var model = GetModel(context);

            foreach (var outgoingMessage in outgoingMessages)
            {
                var destinationAddress = outgoingMessage.DestinationAddress;
                var message = outgoingMessage.TransportMessage;
                var props = model.CreateBasicProperties();
                var headers = message.Headers;
                var timeToBeDelivered = GetTimeToBeReceivedOrNull(message);

                props.Headers = headers
                    .ToDictionary(kvp => kvp.Key, kvp => (object)HeaderValueEncoding.GetBytes(kvp.Value));

                if (timeToBeDelivered.HasValue)
                {
                    props.Expiration = timeToBeDelivered.Value.TotalMilliseconds.ToString("0");
                }

                var express = headers.ContainsKey(Headers.Express);

                props.Persistent = !express;

                var routingKey = new FullyQualifiedRoutingKey(destinationAddress);
                var exchange = routingKey.ExchangeName ?? _directExchangeName;

                // when we're sending point-to-point, we want to be sure that we are never sending the message out into nowhere
                if (exchange == _directExchangeName)
                {
                    EnsureQueueIsInitialized(destinationAddress, model);
                }

                model.BasicPublish(exchange, routingKey.RoutingKey, props, message.Body);
            }
        }

        void EnsureQueueIsInitialized(string destinationAddress, IModel model)
        {
            _initializedQueues
                .GetOrAdd(destinationAddress, _ =>
                {
                    if (_declareInputQueue)
                    {
                        DeclareQueue(destinationAddress, model, true);
                    }

                    if (_bindInputQueue)
                    {
                        BindInputQueue(destinationAddress, model);
                    }
                    return true;
                });
        }

        class FullyQualifiedRoutingKey
        {
            public FullyQualifiedRoutingKey(string destinationAddress)
            {
                if (destinationAddress == null) throw new ArgumentNullException(nameof(destinationAddress));

                var tokens = destinationAddress.Split('@');

                if (tokens.Length > 1)
                {
                    ExchangeName = tokens.Last();
                    RoutingKey = string.Join("@", tokens.Take(tokens.Length - 1));
                }
                else
                {
                    ExchangeName = null;
                    RoutingKey = destinationAddress;
                }
            }

            public string ExchangeName { get; }
            public string RoutingKey { get; }
        }

        static TimeSpan? GetTimeToBeReceivedOrNull(TransportMessage message)
        {
            var headers = message.Headers;
            TimeSpan? timeToBeReceived = null;
            if (headers.ContainsKey(Headers.TimeToBeReceived))
            {
                var timeToBeReceivedStr = headers[Headers.TimeToBeReceived];
                timeToBeReceived = TimeSpan.Parse(timeToBeReceivedStr);
            }
            return timeToBeReceived;
        }

        IModel GetModel(ITransactionContext context)
        {
            var model = context.GetOrAdd(CurrentModelItemsKey, () =>
            {
                var connection = _connectionManager.GetConnection();
                var newModel = connection.CreateModel();
                context.OnDisposed(() => newModel.Dispose());

                return newModel;
            });

            return model;
        }

        class OutgoingMessage
        {
            public OutgoingMessage(string destinationAddress, TransportMessage transportMessage)
            {
                DestinationAddress = destinationAddress;
                TransportMessage = transportMessage;
            }

            public string DestinationAddress { get; }
            public TransportMessage TransportMessage { get; }
        }

        public void Dispose()
        {
            if (_consumer?.Model != null && _consumer.Model.IsOpen)
            {
                _consumer.Model.Dispose();
            }

            _connectionManager.Dispose();
        }

        public async Task<string[]> GetSubscriberAddresses(string topic)
        {
            return new[] { $"{topic}@{_topicExchangeName}" };
        }

        public async Task RegisterSubscriber(string topic, string subscriberAddress)
        {
            var connection = _connectionManager.GetConnection();

            using (var model = connection.CreateModel())
            {
                model.QueueBind(Address, _topicExchangeName, topic);
            }
        }

        public async Task UnregisterSubscriber(string topic, string subscriberAddress)
        {
            var connection = _connectionManager.GetConnection();

            using (var model = connection.CreateModel())
            {
                model.QueueUnbind(Address, _topicExchangeName, topic, new Dictionary<string, object>());
            }
        }

        public bool IsCentralized => true;
    }
}