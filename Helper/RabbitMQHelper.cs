﻿using LightMessager.Common;
using LightMessager.DAL;
using LightMessager.Message;
using LightMessager.Pool;
using NLog;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace LightMessager.Helper
{
    public sealed class RabbitMQHelper
    {
        static ConnectionFactory factory;
        static IConnection connection;
        static List<string> prepersist;
        static ConcurrentDictionary<Type, QueueInfo> dict_info;
        static ConcurrentDictionary<Type, object> dict_fun;
        static ConcurrentDictionary<Type, ObjectPool<IPooledWapper>> pools;
        static readonly ushort prefetch_count;
        static object lockobj = new object();
        static Logger _logger = LogManager.GetLogger("RabbitMQHelper");

        private RabbitMQHelper()
        { }

        static RabbitMQHelper()
        {
            prefetch_count = 5;
            prepersist = new List<string>();
            dict_info = new ConcurrentDictionary<Type, QueueInfo>();
            dict_fun = new ConcurrentDictionary<Type, object>();
            pools = new ConcurrentDictionary<Type, ObjectPool<IPooledWapper>>();
            factory = new ConnectionFactory();
            factory.UserName = "admin";
            factory.Password = "123456";
            factory.VirtualHost = "/";
            factory.HostName = "127.0.0.1";
            factory.Port = 5672;
            factory.AutomaticRecoveryEnabled = true;
            factory.NetworkRecoveryInterval = TimeSpan.FromSeconds(30);
            connection = factory.CreateConnection();
        }

        /// <summary>
        /// 注册消息处理器
        /// </summary>
        /// <typeparam name="TMessage">消息类型</typeparam>
        /// <typeparam name="THandler">消息处理器类型</typeparam>
        /// <param name="delayProcess">延迟多少毫秒开始处理消息（用于并发较大的场景下，由队列分担消息处理端压力的情况）</param>
        public static void RegisterHandler<TMessage, THandler>(int delayProcess = 0)
            where THandler : BaseHandleMessages<TMessage>
            where TMessage : BaseMessage
        {
            try
            {
                var type = typeof(TMessage);
                if (!dict_fun.ContainsKey(type))
                {
                    var obj = dict_fun.GetOrAdd(type, t => Activator.CreateInstance<THandler>()) as THandler;
                    var channel = connection.CreateModel();
                    var consumer = new EventingBasicConsumer(channel);
                    if (delayProcess > 0)
                    {
                        /*
                          @param prefetchSize maximum amount of content (measured in octets) that the server will deliver, 0 if unlimited
                          @param prefetchCount maximum number of messages that the server will deliver, 0 if unlimited
                          @param global true if the settings should be applied to the entire channel rather than each consumer
                        */
                        channel.BasicQos(0, prefetch_count, false);
                    }
                    consumer.Received += async (model, ea) =>
                    {
                        var body = Encoding.UTF8.GetString(ea.Body);
                        var json = Jil.JSON.Deserialize<TMessage>(body);
                        if (delayProcess > 0)
                        {
                            Thread.Sleep(delayProcess);
                        }
                        await obj.Handle(json);
                        if (json.NeedNAck)
                        {
                            channel.BasicNack(ea.DeliveryTag, false, true);
                        }
                        else
                        {
                            channel.BasicAck(ea.DeliveryTag, false);
                        }
                    };

                    var exchange_name = string.Empty;
                    var route_key = string.Empty;
                    var queue_name = string.Empty;
                    EnsureQueue<TMessage>(channel, out exchange_name, out route_key, out queue_name);
                    channel.BasicConsume(queue_name, false, consumer);
                }
            }
            catch (Exception ex)
            {
                //ErrorStore.LogExceptionWithoutContext(ex);
                _logger.Debug("RegisterHandler(int delayProcess = 0)出错，异常：" + ex.Message + "；堆栈：" + ex.StackTrace);
            }
        }

        /// <summary>
        /// 注册消息处理器，并作为subscriber接收消息
        /// </summary>
        /// <typeparam name="TMessage">消息类型</typeparam>
        /// <typeparam name="THandler">消息处理器类型</typeparam>
        /// <param name="subscriberName">subscriber名称</param>
        /// <param name="delayProcess">延迟多少毫秒开始处理消息（用于并发较大的场景下，由队列分担消息处理端压力的情况）</param>
        public static void RegisterHandlerAs<TMessage, THandler>(string subscriberName, int delayProcess = 0)
            where THandler : BaseHandleMessages<TMessage>
            where TMessage : BaseMessage
        {
            try
            {
                if (string.IsNullOrWhiteSpace(subscriberName))
                {
                    throw new ArgumentNullException("subscriberName");
                }

                var type = typeof(TMessage);
                if (!dict_fun.ContainsKey(type))
                {
                    var obj = dict_fun.GetOrAdd(type, t => Activator.CreateInstance<THandler>()) as THandler;
                    var channel = connection.CreateModel();
                    var consumer = new EventingBasicConsumer(channel);
                    if (delayProcess > 0)
                    {
                        /*
                          @param prefetchSize maximum amount of content (measured in octets) that the server will deliver, 0 if unlimited
                          @param prefetchCount maximum number of messages that the server will deliver, 0 if unlimited
                          @param global true if the settings should be applied to the entire channel rather than each consumer
                        */
                        channel.BasicQos(0, prefetch_count, false);
                    }
                    consumer.Received += async (model, ea) =>
                    {
                        var body = Encoding.UTF8.GetString(ea.Body);
                        var json = Jil.JSON.Deserialize<TMessage>(body);
                        if (delayProcess > 0)
                        {
                            Thread.Sleep(delayProcess);
                        }
                        await obj.Handle(json);
                        if (json.NeedNAck)
                        {
                            channel.BasicNack(ea.DeliveryTag, false, true);
                        }
                        else
                        {
                            channel.BasicAck(ea.DeliveryTag, false);
                        }
                    };

                    var exchange_name = string.Empty;
                    var route_key = string.Empty;
                    var queue_name = string.Empty;
                    EnsureQueue<TMessage>(channel, out exchange_name, subscriberName);
                    channel.BasicConsume(subscriberName + "." + type.Name + ".input", false, consumer);
                }
            }
            catch (Exception ex)
            {
                //ErrorStore.LogExceptionWithoutContext(ex);
                _logger.Debug("RegisterHandler(string subscriberName, int delayProcess = 0)出错，异常：" + ex.Message + "；堆栈：" + ex.StackTrace);
            }
        }

        /// <summary>
        /// 发送一条消息
        /// </summary>
        /// <typeparam name="TMessage">消息类型</typeparam>
        /// <param name="message">消息</param>
        /// <param name="delaySend">延迟多少毫秒发送消息</param>
        /// <returns>发送成功返回true，否则返回false</returns>
        public static bool Send<TMessage>(TMessage message, int delaySend = 0)
            where TMessage : BaseMessage
        {
            if (string.IsNullOrWhiteSpace(message.Source))
            {
                throw new ArgumentException("message.Source不允许为空");
            }

            var msgId = string.Empty;
            if (!PrePersistMessage(message, out msgId))
            {
                return false;
            }

            using (var pooled = InnerCreateChannel<TMessage>())
            {
                IModel channel = pooled.Channel;
                pooled.PreRecord(msgId);

                var exchange_name = string.Empty;
                var route_key = string.Empty;
                var queue_name = string.Empty;
                if (delaySend > 0)
                {
                    EnsureQueue<TMessage>(channel, delaySend, out exchange_name, out route_key, out queue_name);
                }
                else
                {
                    EnsureQueue<TMessage>(channel, out exchange_name, out route_key, out queue_name);
                }

                message.ID = msgId;
                var json_str = Jil.JSON.SerializeDynamic(message, Jil.Options.IncludeInherited);
                var bytes = Encoding.UTF8.GetBytes(json_str);
                var props = channel.CreateBasicProperties();
                props.ContentType = "text/plain";
                props.DeliveryMode = 2;
                channel.BasicPublish(exchange_name, route_key, props, bytes);
                channel.WaitForConfirms();
            }

            return true;
        }

        /// <summary>
        /// 发布消息到指定的subscriber
        /// </summary>
        /// <typeparam name="TMessage">消息类型</typeparam>
        /// <param name="message">消息</param>
        /// <param name="subscriberNames">subscriber名称</param>
        /// <returns>发送成功返回true，否则返回false</returns>
        public static bool Publish<TMessage>(TMessage message, int delaySend = 0, params string[] subscriberNames)
            where TMessage : BaseMessage
        {
            if (string.IsNullOrWhiteSpace(message.Source))
            {
                throw new ArgumentException("message.Source不允许为空");
            }

            if (subscriberNames == null || subscriberNames.Length == 0)
            {
                throw new ArgumentException("subscriberNames不允许为空");
            }

            if (subscriberNames.Length == 1)
            {
                return Send(message);
            }

            var msgId = string.Empty;
            if (!PrePersistMessage(message, out msgId))
            {
                return false;
            }

            using (var pooled = InnerCreateChannel<TMessage>())
            {
                IModel channel = pooled.Channel;
                pooled.PreRecord(msgId);

                var exchange_name = string.Empty;
                var route_key = string.Empty;
                if (delaySend > 0)
                {
                    EnsureQueue<TMessage>(channel, delaySend, out exchange_name, out route_key, subscriberNames);
                }
                else
                {
                    EnsureQueue<TMessage>(channel, out exchange_name, subscriberNames);
                }

                message.ID = msgId;
                var json_str = Jil.JSON.SerializeDynamic(message, Jil.Options.IncludeInherited);
                var bytes = Encoding.UTF8.GetBytes(json_str);
                var props = channel.CreateBasicProperties();
                props.ContentType = "text/plain";
                props.DeliveryMode = 2;
                if (delaySend > 0)
                {
                    channel.BasicPublish(exchange_name, route_key, props, bytes);
                    channel.WaitForConfirms();
                }
                else
                {
                    foreach (var subscriber in subscriberNames)
                    {
                        channel.BasicPublish(exchange_name, "topic." + subscriber, props, bytes);
                        channel.WaitForConfirms();
                    }
                }
            }

            return true;
        }

        private static PooledChannel InnerCreateChannel<TMessage>()
            where TMessage : BaseMessage
        {
            var pool = pools.GetOrAdd(typeof(TMessage), t => new ObjectPool<IPooledWapper>(p => new PooledChannel(connection.CreateModel(), p), 10));
            return pool.Get() as PooledChannel;
        }

        private static bool PrePersistMessage<TMessage>(TMessage message, out string knuthHash)
            where TMessage : BaseMessage
        {
            if (!string.IsNullOrWhiteSpace(message.ID))
            {
                knuthHash = message.ID;
            }
            else
            {
                knuthHash = MessageIdHelper.GenerateMessageIdFrom(Encoding.UTF8.GetBytes(message.Source));
            }

            if (prepersist.Contains(knuthHash))
            {
                return false;
            }
            else
            {
                prepersist.Add(knuthHash);
                lock (lockobj)
                {
                    if (prepersist.Count > 1000)
                    {
                        prepersist.RemoveRange(0, 950);
                    }
                }

                var model = MessageQueueHelper.GetModelBy(knuthHash);
                if (model != null)
                {
                    return false;
                }
                else
                {
                    var now = DateTime.Now;
                    var newmodel = new MessageQueue
                    {
                        KnuthHash = knuthHash,
                        CanBeRemoved = false,
                        CreatedTime = now,
                        ExecuteCount = 0,
                        LastExecuteTime = now,
                        MsgContent = message.Source
                    };
                    MessageQueueHelper.Insert(newmodel);
                    return true;
                }
            }
        }

        private static void EnsureQueue<TMessage>(IModel channel, out string exchangeName, out string routeKey, out string queueName)
            where TMessage : BaseMessage
        {
            var type = typeof(TMessage);
            if (!dict_info.ContainsKey(type))
            {
                var info = GetQueueInfo(type);
                exchangeName = info.ExchangeName;
                routeKey = info.RouteKeyName;
                queueName = info.QueueName;

                channel.ExchangeDeclare(exchangeName, ExchangeType.Direct);
                channel.QueueDeclare(queueName, false, false, false);
                channel.QueueBind(queueName, exchangeName, routeKey);
            }
            else
            {
                var info = GetQueueInfo(type);
                exchangeName = info.ExchangeName;
                queueName = info.QueueName;
                routeKey = info.RouteKeyName;
            }
        }

        private static void EnsureQueue<TMessage>(IModel channel, int delaySend, out string exchangeName, out string routeKey, out string queueName)
            where TMessage : BaseMessage
        {
            var type = typeof(DelayTypeWapper<TMessage>);
            if (!dict_info.ContainsKey(type))
            {
                var info = GetQueueInfo(type);
                exchangeName = info.ExchangeName;
                routeKey = info.RouteKeyName;
                queueName = info.QueueName;
                channel.ExchangeDeclare(exchangeName, ExchangeType.Direct);
                channel.QueueDeclare(queueName, false, false, false);
                channel.QueueBind(queueName, exchangeName, routeKey);

                var args = new Dictionary<string, object>();
                args.Add("x-message-ttl", delaySend);
                args.Add("x-dead-letter-exchange", exchangeName);
                args.Add("x-dead-letter-routing-key", queueName);
                channel.QueueDeclare(info.QueueName + ".delay", false, false, false, args);
                exchangeName = string.Empty;
                routeKey = info.RouteKeyName + ".delay";
                queueName = info.QueueName + ".delay";
            }
            else
            {
                var info = GetQueueInfo(type);
                exchangeName = string.Empty;
                routeKey = info.RouteKeyName + ".delay";
                queueName = info.QueueName + ".delay";
            }
        }

        private static void EnsureQueue<TMessage>(IModel channel, out string exchangeName, params string[] subscriberNames)
            where TMessage : BaseMessage
        {
            var type = typeof(TMessage);
            if (!dict_info.ContainsKey(type))
            {
                var info = GetQueueInfo(type);
                exchangeName = "topic." + info.ExchangeName;
                channel.ExchangeDeclare(exchangeName, ExchangeType.Topic);
                foreach (var subscriber in subscriberNames)
                {
                    channel.QueueDeclare(subscriber + "." + info.QueueName, false, false, false);
                    channel.QueueBind(subscriber + "." + info.QueueName, exchangeName, "topic." + subscriber);
                }
            }
            else
            {
                var info = GetQueueInfo(type);
                exchangeName = "topic." + info.ExchangeName;
            }
        }

        private static void EnsureQueue<TMessage>(IModel channel, int delaySend, out string exchangeName, out string routeKey, params string[] subscriberNames)
            where TMessage : BaseMessage
        {
            var type = typeof(DelayTypeWapper<TMessage>);
            if (!dict_info.ContainsKey(type))
            {
                var info = GetQueueInfo(type);
                exchangeName = "topic." + info.ExchangeName;
                channel.ExchangeDeclare(exchangeName, ExchangeType.Topic);
                foreach (var subscriber in subscriberNames)
                {
                    channel.QueueDeclare(subscriber + "." + info.QueueName, false, false, false);
                    channel.QueueBind(subscriber + "." + info.QueueName, exchangeName, "topic." + subscriber);
                }

                #region inner_input
                channel.ExchangeDeclare("inner_delay_exchange", ExchangeType.Direct);
                channel.QueueDeclare("inner_delay_input", false, false, false);
                channel.QueueBind("inner_delay_input", "inner_delay_exchange", "inner_delay_input");
                var consumer = new EventingBasicConsumer(channel);
                consumer.Received += (model, ea) =>
                {
                    var p = channel.CreateBasicProperties();
                    p.ContentType = "text/plain";
                    p.DeliveryMode = 2;
                    foreach (var subscriber in subscriberNames)
                    {
                        channel.BasicPublish("topic." + info.ExchangeName, "topic." + subscriber, p, ea.Body);
                    }
                    channel.BasicAck(ea.DeliveryTag, false);
                };
                channel.BasicConsume("inner_delay_input", false, consumer);
                #endregion

                var args = new Dictionary<string, object>();
                args.Add("x-message-ttl", delaySend);
                args.Add("x-dead-letter-exchange", "inner_delay_exchange");
                args.Add("x-dead-letter-routing-key", "inner_delay_input");
                channel.QueueDeclare(info.QueueName + ".delay", false, false, false, args);
                exchangeName = string.Empty;
                routeKey = info.QueueName + ".delay";
            }
            else
            {
                var info = GetQueueInfo(type);
                exchangeName = string.Empty;
                routeKey = string.Empty; // 此种情况下不在意routeKey
            }
        }

        private static QueueInfo GetQueueInfo(Type messageType)
        {
            var type_name = messageType.IsGenericType ? messageType.GenericTypeArguments[0].Name : messageType.Name;
            var info = dict_info.GetOrAdd(messageType, t => new QueueInfo
            {
                ExchangeName = type_name + ".exchange",
                RouteKeyName = type_name + ".input",
                QueueName = type_name + ".input"
            });

            return info;
        }

        private class DelayTypeWapper<TMessage>
            where TMessage : BaseMessage
        {
        }

        private class QueueInfo
        {
            public string ExchangeName;
            public string RouteKeyName;
            public string QueueName;
        }
    }
}
