﻿using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Container;
using R.MessageBus.Interfaces;
using R.MessageBus.Persistance.MongoDb;
using Xunit;

namespace R.MessageBus.IntegrationTests.Bus
{
    public class BusSetupTests
    {
        [Fact]
        public void ShouldSetupBusWithDefaultConfiguration()
        {
            // Arrange
            IBus bus = MessageBus.Bus.Initialize();

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal(typeof(Consumer), configuration.ConsumerType);
            Assert.Equal(typeof(Publisher), configuration.PublisherType);
            Assert.Equal(typeof(StructuremapContainer), configuration.Container);
            Assert.Equal(typeof(MongoDbProcessManagerFinder), configuration.ProcessManagerFinder);
        }

        [Fact]
        public void ShouldSetupBusWithCustomConfigurationFile()
        {
            // Arrange
            IBus bus = MessageBus.Bus.Initialize(config => config.LoadSettings(@"Bus/TestConfiguration.xml", "TestEndPoint2"));

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal("TestDatabase", configuration.PersistenceStoreDatabaseName);
            Assert.Equal("TestExchange2", configuration.TransportSettings.Exchange.Name);
            Assert.False(configuration.TransportSettings.Exchange.Durable);
            Assert.True(configuration.TransportSettings.Exchange.AutoDelete);
            Assert.Equal(2, configuration.TransportSettings.MaxRetries);
            Assert.Equal(2000, configuration.TransportSettings.RetryDelay);
            Assert.Equal("TestQueue1", configuration.TransportSettings.Queue.Name);
            Assert.Equal("TestQueueRoutingKey1", configuration.TransportSettings.Queue.RoutingKey);
            Assert.True(configuration.TransportSettings.Queue.Durable);
            Assert.True(configuration.TransportSettings.Queue.AutoDelete);
            Assert.True(configuration.TransportSettings.Queue.Exclusive);
        }

        [Fact]
        public void ShouldSetupBusWithCustomDatabaseNameOverridingConfigFileSetting()
        {
            // Arrange
            IBus bus = MessageBus.Bus.Initialize(config =>
            {
                config.LoadSettings(@"Bus/TestConfiguration.xml", "TestEndPoint2");
                config.PersistenceStoreDatabaseName = "NewDatabase";
            });

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal("NewDatabase", configuration.PersistenceStoreDatabaseName);
        }
    }
}