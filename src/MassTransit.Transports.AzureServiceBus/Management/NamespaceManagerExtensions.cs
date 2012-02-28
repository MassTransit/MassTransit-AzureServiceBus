// Copyright 2012 Henrik Feldt
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use 
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed 
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.

using System;
using System.Threading.Tasks;
using Magnum.Extensions;
using Magnum.Policies;
using MassTransit.Logging;
using MassTransit.Transports.AzureServiceBus.Internal;
using MassTransit.Transports.AzureServiceBus.Util;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using QueueDescription = MassTransit.AzureServiceBus.QueueDescription;
using SBQDesc = Microsoft.ServiceBus.Messaging.QueueDescription;
using SBTDesc = Microsoft.ServiceBus.Messaging.TopicDescription;
using SBSDesc = Microsoft.ServiceBus.Messaging.SubscriptionDescription;
using SBQClient = Microsoft.ServiceBus.Messaging.QueueClient;
using TopicDescription = MassTransit.AzureServiceBus.TopicDescription;

namespace MassTransit.Transports.AzureServiceBus.Management
{
	/// <summary>
	/// 	Wrapper over the service bus API that provides a limited amount of retry logic and wraps the APM pattern methods into tasks.
	/// </summary>
	public static class NamespaceManagerExtensions
	{
		static readonly ILog _logger = Logger.Get("MassTransit.Transports.AzureServiceBus.Management");

		public static Task TryCreateSubscription([NotNull] this NamespaceManager namespaceManager,
		                                         [NotNull] SubscriptionDescription description)
		{
			return Task.Factory
				.FromAsync<SBSDesc, SBSDesc>(
					namespaceManager.BeginCreateSubscription,
					namespaceManager.EndCreateSubscription,
					description.IDareYou, null);
		}

		public static Task TryDeleteSubscription([NotNull] this NamespaceManager namespaceManager, [NotNull] SubscriptionDescription description)
		{
			if (namespaceManager == null)
				throw new ArgumentNullException("namespaceManager");
			if (description == null)
				throw new ArgumentNullException("description");

			var exists = Task.Factory
				.FromAsync<string, string, bool>(namespaceManager.BeginSubscriptionExists,
				                                 namespaceManager.EndSubscriptionExists,
				                                 description.TopicPath, description.Name, null);

			Func<Task> delete =
				() => Task.Factory
				      	.FromAsync(namespaceManager.BeginDeleteSubscription,
				      	           namespaceManager.EndDeleteSubscription,
				      	           description.TopicPath, description.Name, null)
						.IgnoreExOf<MessagingEntityNotFoundException>();

			return exists.Then(e => e ? delete() : new Task(() => { }));
		}

		public static Task TryDeleteTopic(this NamespaceManager nsm, TopicDescription topic)
		{
			_logger.Debug(string.Format("being topic exists @ {0}", topic.Path));
			var exists = Task.Factory.FromAsync<string, bool>(
				nsm.BeginTopicExists, nsm.EndTopicExists, topic.Path, null)
				.ContinueWith(tExists =>
					{
						_logger.Debug(string.Format("end topic exists @ {0}", topic.Path));
						return tExists.Result;
					});

			return exists.ContinueWith(tExists =>
				{
					_logger.Debug(string.Format("begin delete topic @ {0}", topic.Path));
					// according to documentation this thing doesn't throw anything??!
					return Task.Factory.FromAsync(nsm.BeginDeleteTopic, nsm.EndDeleteTopic, topic.Path, null)
						.ContinueWith(endDelete =>
							{
								_logger.Debug(string.Format("end delete topic @ {0}", topic.Path));
								return endDelete;
							})
						.IgnoreExOf<MessagingEntityNotFoundException>();
				});
		}

		// this one is messed up due to a missing API
		public static Task<TopicClient> TryCreateTopicClient(this MessagingFactory messagingFactory,
		                                                     NamespaceManager nm,
		                                                     Topic topic)
		{
			var timeoutPolicy = ExceptionPolicy
				.InCaseOf<TimeoutException>()
				.CircuitBreak(50.Milliseconds(), 5);

			return Task.Factory.StartNew<TopicClient>(() => new TopicClientImpl(messagingFactory, nm));
		}

		public static Task<QueueClient> TryCreateQueueClient(
			[NotNull] this MessagingFactory mf,
			[NotNull] NamespaceManager nm,
			[NotNull] QueueDescription description,
			int prefetchCount)
		{
			if (mf == null) throw new ArgumentNullException("mf");
			if (description == null) throw new ArgumentNullException("description");

			//return Task.Factory.FromAsync<string, MessageReceiver>(
			//    mf.BeginCreateMessageReceiver,
			//    mf.EndCreateMessageReceiver,
			//    description.Path, null);
			// where's the BeginCreateQueueClient??!

			return Task.Factory.StartNew(() =>
				{
					Func<SBQClient> queue_client = () =>
						{
							var qc = mf.CreateQueueClient(description.Path);
							qc.PrefetchCount = prefetchCount;
							return qc;
						};

					Func<Task<SBQClient>> drain = () =>
						{
							return nm.TryDeleteQueue(description.Path)
								.ContinueWith(tDel => nm.TryCreateQueue(description)).Unwrap()
								.ContinueWith(tCreate => queue_client());
						};

					return new QueueClientImpl(queue_client(), drain) as QueueClient;
				});
		}

		public static Task<QueueDescription> TryCreateQueue(this NamespaceManager nsm, string queueName)
		{
			return TryCreateQueue(nsm, new QueueDescriptionImpl(queueName));
		}
		public static Task<QueueDescription> TryCreateQueue(this NamespaceManager nsm, QueueDescription queueDescription)
		{
			//if (nsm.GetQueue(queueName) == null) 
			// bugs out http://social.msdn.microsoft.com/Forums/en-US/windowsazureconnectivity/thread/6ce20f60-915a-4519-b7e3-5af26fc31e35
			// says it'll give null, but throws!
			
			return ExistsQueue(nsm, queueDescription.Path)
				.Then(doesExist => doesExist ? GetQueue(nsm, queueDescription.Path) 
											 : CreateQueue(nsm, queueDescription));
		}

		public static Task TryDeleteQueue(this NamespaceManager nm, string queueName)
		{
			return ExistsQueue(nm, queueName)
				.Then(doesExist => doesExist ? DeleteQueue(nm, queueName) 
											 : Task.Factory.StartNew(() => { }));
		}

		static Task<bool> ExistsQueue(NamespaceManager nsm, string queueName)
		{
			_logger.Debug(string.Format("being queue exists @ '{0}'", queueName));
			return Task.Factory.FromAsync<string, bool>(
				nsm.BeginQueueExists, nsm.EndQueueExists, queueName, null)
				.ContinueWith(tExists =>
					{
						_logger.Debug(string.Format("end queue exists @ '{0}'", queueName));
						return tExists.Result;
					});
		}

		static Task<QueueDescription> CreateQueue(NamespaceManager nsm, QueueDescription description)
		{
			_logger.Debug(string.Format("being create queue @ '{0}'", description.Path));
			return Task.Factory.FromAsync<SBQDesc, SBQDesc>(
				nsm.BeginCreateQueue, nsm.EndCreateQueue, description.Inner, null)
				.ContinueWith(tCreate =>
					{
						_logger.Debug(string.Format("end create queue @ '{0}'", description.Path));
						return new QueueDescriptionImpl(tCreate.Result) as QueueDescription;
					});
		}

		static Task<QueueDescription> GetQueue(NamespaceManager nsm, string queueName)
		{
			_logger.Debug(string.Format("begin get queue @ '{0}'", queueName));
			return Task.Factory.FromAsync<string, SBQDesc>(
				nsm.BeginGetQueue, nsm.EndGetQueue, queueName, null)
				.ContinueWith(tGet =>
					{
						_logger.Debug(string.Format("end get queue @ '{0}'", queueName));
						return new QueueDescriptionImpl(tGet.Result) as QueueDescription;
					});
		}

		static Task DeleteQueue(NamespaceManager nm, string queueName)
		{
			_logger.Debug(string.Format("being delete queue @ '{0}'", queueName));
			return Task.Factory.FromAsync(
				nm.BeginDeleteQueue, 
				nm.EndDeleteQueue, queueName, null)
				.ContinueWith(tDel => _logger.Debug(string.Format("end delete queue @ '{0}'", queueName)));
		}
		
		/// <returns> the topic description </returns>
		public static Task<Topic> TryCreateTopic(this NamespaceManager nm,
		                                         MessagingFactory factory,
		                                         string topicName)
		{
			_logger.Debug(string.Format("begin topic exists @ '{0}'", topicName));
			var exists = Task.Factory.FromAsync<string, bool>(
				nm.BeginTopicExists, nm.EndTopicExists, topicName, null)
				.ContinueWith(tExists =>
					{
						_logger.Debug(string.Format("end topic exists @ '{0}'", topicName));
						return tExists.Result;
					});

			Func<Task<Topic>> create = () =>
				{
					_logger.Debug(string.Format("begin create topic @ '{0}'", topicName));
					return Task.Factory.FromAsync<string, SBTDesc>(
						nm.BeginCreateTopic, nm.EndCreateTopic, topicName, null)
						.ContinueWith(tCreate =>
							{
								_logger.Debug(string.Format("end create topic @ '{0}'", topicName));
								return new TopicImpl(nm, factory, new TopicDescriptionImpl(tCreate.Result)) as Topic;
							});
				};

			Func<Task<Topic>> get = () =>
				{
					_logger.Debug(string.Format("begin get topic @ '{0}'", topicName));
					return Task.Factory.FromAsync<string, SBTDesc>(
						nm.BeginGetTopic, nm.EndGetTopic, topicName, null)
						.ContinueWith(tGet =>
							{
								_logger.Debug(string.Format("end get topic @ '{0}'", topicName));
								return new TopicImpl(nm, factory, new TopicDescriptionImpl(tGet.Result)) as Topic;
							});
				};

			return exists.Then(doesExist => doesExist ? get() : create());

			//while (true)
			//{
			//    try
			//    {
			//        if (!nm.TopicExists(topicName))
			//            return new TopicImpl(nm, factory, nm.CreateTopic(topicName));
			//    }
			//    catch (MessagingEntityAlreadyExistsException)
			//    {
			//    }
			//    try
			//    {
			//        return new TopicImpl(nm, factory, nm.GetTopic(topicName));
			//    }
			//    catch (MessagingEntityNotFoundException) // someone beat us to removing it
			//    {
			//    }
			//}
		}
	}
}