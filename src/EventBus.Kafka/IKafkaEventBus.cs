namespace CleanOnionArchitecture.EventBus.Kafka;

using System.Threading.Tasks;

/// <summary>
/// Provides requirements for which one is related with Kafka 
/// </summary>
public interface IKafkaEventBus : IEventBus
{
    /// <summary>
    /// Creates a default topic on kafka. 
    /// </summary>
    /// <param name="eventName">Name of the topic</param>
    /// <returns>Returns Task to support async</returns>
    Task CreateTopicAsync(string topicName);

}