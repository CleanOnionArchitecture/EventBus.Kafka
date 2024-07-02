namespace CleanOnionArchitecture.EventBus.Kafka;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public class KafkaEventBus : IKafkaEventBus
{
    private const string DEAD_LETTER_TOPIC_NAME = "DeadLetter";
    private const int DELAY = 1;
    private const int PASSED_TIME = 10;


    private readonly int RETRY_COUNT;
    private readonly bool ENABLE_DEAD_LETTER;
    private readonly bool ENABLE_FLUSH;
    private readonly ushort FLUSH_TIMEOUT;
    private readonly ILogger<IEventBus> _logger;
    private readonly ISubscriptionManager _eventBusSubscriptionManager;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IProducer<Null, string> _producer;

    private readonly ProducerConfig _producerConfig;
    private readonly ConsumerConfig _consumerConfig;
    private readonly ConsumerBuilder<Null, string> _consumerBuilder;

    public KafkaEventBus(
        ILogger<IEventBus> logger
        , ISubscriptionManager eventBusSubscriptionManager
        , IServiceScopeFactory serviceScopeFactory
        , KafkaServiceConfiguration kafkaServiceConfiguration
    )
    {
        _logger = logger;
        _eventBusSubscriptionManager = eventBusSubscriptionManager;
        _serviceScopeFactory = serviceScopeFactory;
        RETRY_COUNT = kafkaServiceConfiguration.RetryCount;
        ENABLE_DEAD_LETTER = kafkaServiceConfiguration.EnableDeadLetter;
        ENABLE_FLUSH = kafkaServiceConfiguration.EnableFlush;
        FLUSH_TIMEOUT = kafkaServiceConfiguration.FlushTimeout;

        _producerConfig = new ProducerConfig(new ClientConfig()
        {
            BootstrapServers = $"{kafkaServiceConfiguration.Server}:{kafkaServiceConfiguration.Port}"
        });

        if (kafkaServiceConfiguration.IsUsingAuthentication.Value)
        {
            if (Enum.TryParse(kafkaServiceConfiguration.SaslMechanism, true, out SaslMechanism mechanismValue))
            {
                _producerConfig.SaslMechanism = mechanismValue;
            }

            if (Enum.TryParse(kafkaServiceConfiguration.SecurityProtocol, true,
                    out SecurityProtocol protocolValue))
            {
                _producerConfig.SecurityProtocol = protocolValue;
            }

            _producerConfig.SaslUsername = kafkaServiceConfiguration.Username;
            _producerConfig.SaslPassword = kafkaServiceConfiguration.Password;
        }

        _consumerConfig = new ConsumerConfig(new ClientConfig()
        {
            BootstrapServers = $"{kafkaServiceConfiguration.Server}:{kafkaServiceConfiguration.Port}"
        })
        {
            GroupId = $"{kafkaServiceConfiguration.ConsumerGroupId}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        };

        if (kafkaServiceConfiguration.IsUsingAuthentication.Value)
        {
            if (Enum.TryParse(kafkaServiceConfiguration.SaslMechanism, true, out SaslMechanism mechanismValue))
            {
                _consumerConfig.SaslMechanism = mechanismValue;
            }

            if (Enum.TryParse(kafkaServiceConfiguration.SecurityProtocol, true,
                    out SecurityProtocol protocolValue))
            {
                _consumerConfig.SecurityProtocol = protocolValue;
            }

            _consumerConfig.SaslUsername = kafkaServiceConfiguration.Username;
            _consumerConfig.SaslPassword = kafkaServiceConfiguration.Password;
        }

        _consumerBuilder = new ConsumerBuilder<Null, string>(_consumerConfig);
        _producer = new ProducerBuilder<Null, string>(_producerConfig).Build();
    }

    public async Task PublishAsync<TEventType>(TEventType @event) where TEventType : IEvent
    {
        try
        {
            string eventName = typeof(TEventType).Name;
            await CreateTopicAsync(eventName);
            string serializedValue = JsonSerializer.Serialize(@event);
            DeliveryResult<Null, string>? deliveryResult = await _producer.ProduceAsync(topic: eventName
                , new Message<Null, string>()
                {
                    Value = @serializedValue
                }
            );


            if (deliveryResult.Status != PersistenceStatus.Persisted)
            {
                await RetryFailedEvent(serializedValue, eventName, _producer);
            }

            if (ENABLE_FLUSH)
                _producer.Flush(TimeSpan.FromSeconds(FLUSH_TIMEOUT));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error while publishing: {Message}", exception.Message);
            throw;
        }
    }

    public async Task SubscribeAsync<TEventType, TEventHandlerType>(CancellationToken cancellationToken)
        where TEventType : IEvent
        where TEventHandlerType : class, IEventHandler<TEventType>
    {
        using (IConsumer<Null, string> consumer = _consumerBuilder.Build())
        {
            string eventName = typeof(TEventType).Name;
            _eventBusSubscriptionManager.Subscribe<TEventType, TEventHandlerType>();
            await CreateTopicAsync(eventName);
            consumer.Subscribe(topic: eventName);
            // TODO @salih => buradaki looptask'ın yaşam döngüsü ve diğer handler'ın da call edilmesi için geniş bir zamanda test edilmesi gerekiyor
            await Task.Run(async () =>
            {
                do
                {
                    ConsumeResult<Null, string>? consumeResult = null;
                    try
                    {
                        consumeResult = consumer.Consume();
                        if (_eventBusSubscriptionManager.HasEvent<TEventType>())
                        {
                            using IServiceScope scope = _serviceScopeFactory.CreateScope();
                            TEventHandlerType eventHandler = scope.ServiceProvider.GetRequiredService<TEventHandlerType>();
                            TEventType eventObj = JsonSerializer.Deserialize<TEventType>(consumeResult.Message.Value);
                            await eventHandler.HandleEvent(eventObj, cancellationToken);
                        }
                    }
                    catch (JsonException jsonException)
                    {
                        _logger.LogError(jsonException,
                            "Error while subscribing: {Message} \n Json: {ConsumeResultMessage}",
                            jsonException.Message,
                            consumeResult.Message.Value);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "Exception occured while consuming {EventName}", eventName);
                        if (ENABLE_DEAD_LETTER)
                        {
                            using IServiceScope scope = _serviceScopeFactory.CreateScope();
                            TEventType eventObj = JsonSerializer.Deserialize<TEventType>(consumeResult.Message.Value) ?? throw new NullReferenceException();
                            await this.PublishDeadLetterEventAsync(eventObj);
                        }
                    }
                } while (true);
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CreateTopicAsync(string eventName)
    {
        try
        {
            IAdminClient adminClient = new AdminClientBuilder(_producerConfig).Build();
            List<TopicMetadata>? topics = adminClient.GetMetadata(TimeSpan.FromSeconds(PASSED_TIME)).Topics;
            if (!topics.Any(x => x.Topic.Equals(eventName)))
            {
                TopicSpecification topicSpecification = new TopicSpecification();
                topicSpecification.Name = eventName;
                await adminClient.CreateTopicsAsync(new[] { topicSpecification });
            }
        }
        catch (CreateTopicsException createTopicsException)
        {
            _logger.LogWarning(createTopicsException, "{Message}", createTopicsException.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error creating topic. {Message}", exception.Message);
            throw;
        }
    }

    private async Task PublishDeadLetterEventAsync<TEventType>(TEventType @event) where TEventType : IEvent
    {
        try
        {
            IProducer<Null, string> producer = new ProducerBuilder<Null, string>(_producerConfig).Build();
            await CreateDeadLetterTopic();
            string serializedValue = JsonSerializer.Serialize(@event);
            DeliveryResult<Null, string>? deliveryResult = await producer.ProduceAsync(topic: DEAD_LETTER_TOPIC_NAME
                , new Message<Null, string>()
                {
                    Value = @serializedValue
                }
            );


            if (deliveryResult.Status != PersistenceStatus.Persisted)
            {
                await RetryFailedEvent(serializedValue, DEAD_LETTER_TOPIC_NAME, producer);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error while publishing Dead Letter: {Message}", exception.Message);
            throw;
        }
    }

    private async Task CreateDeadLetterTopic()
    {
        try
        {
            IAdminClient adminClient = new AdminClientBuilder(_producerConfig).Build();
            List<TopicMetadata>? topics = adminClient.GetMetadata(TimeSpan.FromSeconds(PASSED_TIME)).Topics;
            if (!topics.Any(x => x.Topic.Equals(DEAD_LETTER_TOPIC_NAME)))
            {
                TopicSpecification topicSpecification = new TopicSpecification();
                topicSpecification.Name = DEAD_LETTER_TOPIC_NAME;
                await adminClient.CreateTopicsAsync(new[] { topicSpecification });
            }
        }
        catch (CreateTopicsException createTopicsException)
        {
            _logger.LogWarning(createTopicsException, "{Message}", createTopicsException.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error while creating Dead Letter topic. {Message}", exception.Message);
            throw;
        }
    }

    private async Task RetryFailedEvent(string serializedValue, string eventName, IProducer<Null, string> producer)
    {
        int retries = 0;
        while (retries <= RETRY_COUNT)
        {
            retries++;
            DeliveryResult<Null, string>? retryDeliveryResult = await producer.ProduceAsync(topic: eventName
                , new Message<Null, string>()
                {
                    Value = @serializedValue
                }
            );


            if (retryDeliveryResult.Status == PersistenceStatus.Persisted || retries == RETRY_COUNT) break;
            Thread.Sleep(TimeSpan.FromMinutes(DELAY) * retries);
        }

        if (retries == RETRY_COUNT)
            _logger.LogError("Could not delivered {EventName} to Kafka Cluster tried {RetryCount} times", eventName, retries);
    }
}