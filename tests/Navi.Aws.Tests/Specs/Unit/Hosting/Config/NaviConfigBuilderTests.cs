using Amazon.Runtime;
using AutoBogus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Navi;
using Navi.Aws.Tests.Builders;
using Navi.Aws.Tests.TestUtils;
using Navi.Aws.Tests.TestUtils.Fixtures;
using Navi.Hosting;
using Navi.Hosting.Config;
using Navi.Models;

namespace Navi.Aws.Tests.Specs.Unit.Hosting.Config;

public class NaviConfigBuilderTests : BaseTest
{
    [Test]
    public async Task ShouldMapProducers()
    {
        var services = new ServiceCollection();
        var client = A.Fake<IProducerClient>();
        services
            .AddSingleton(client)
            .AddSingleton(Options.Create(new NaviConfig()));

        var config = new NaviConfigBuilder(services);
        config.MapTopic<TestMessage>("test-topic");

        var sp = services.BuildServiceProvider();
        var producer = sp.GetRequiredService<IProducer<TestMessage>>();

        var message = TestMessage.New();
        await producer.TryPublish(message);

        A.CallTo(() =>
                client.Publish("test-topic", message, null, A<ProduceOptions>._, default))
            .MustHaveHappened();
    }

    [Test]
    public void ShouldMapDelegateConsumers()
    {
        var services = new ServiceCollection();

        var polling = faker.Date.Timespan();
        var timeout = faker.Date.Timespan();
        var concurrency = faker.Random.Int(1);

        services.AddNavi(nav =>
        {
            nav.Source = "source";
            nav.MapTopic<TestMessage>("test_topic")
                .WithConsumer((TestMessage message) => { })
                .Configure(polling, concurrency)
                .WithTimeout(timeout);
        });

        var sp = services.BuildServiceProvider();
        var describers = sp.GetService<IEnumerable<IConsumerDescriber>>();
        describers.Should().BeEquivalentTo(new[]
        {
            new ConsumerDescriber("test_topic", typeof(DelegateConsumer<TestMessage>),
                typeof(TestMessage),
                new ConsumerConfig
                {
                    MaxConcurrency = concurrency,
                    PollingInterval = polling,
                    ConsumeTimeout = timeout,
                    NameOverride = new(),
                }),
        });
    }

    [Test]
    public void ShouldThrowMapDelegateConsumersWithoutMessageParam()
    {
        var services = new ServiceCollection();

        var polling = faker.Date.Timespan();
        var concurrency = faker.Random.Int(1);

        var action = () =>
            services.AddNavi(nav =>
            {
                nav.MapTopic<TestMessage>("test_topic")
                    .WithConsumer(() => { })
                    .Configure(polling, concurrency);
            });

        action.Should()
            .Throw<NaviException>()
            .WithMessage("No parameter of type*");
    }

    [Test]
    public void ShouldMapConsumers()
    {
        var services = new ServiceCollection();

        var polling = faker.Date.Timespan();
        var timeout = faker.Date.Timespan();
        var concurrency = faker.Random.Int(1);

        services.AddNavi(nav =>
        {
            nav.Source = "source";
            nav.MapTopic<TestMessage>("test_topic")
                .WithConsumer<TestConsumer>()
                .WithTimeout(timeout)
                .Configure(polling, concurrency);
        });

        var sp = services.BuildServiceProvider();
        var describers = sp.GetService<IEnumerable<IConsumerDescriber>>();
        describers.Should().BeEquivalentTo(new[]
        {
            new ConsumerDescriber("test_topic", typeof(TestConsumer), typeof(TestMessage),
                new ConsumerConfig
                {
                    MaxConcurrency = concurrency,
                    PollingInterval = polling,
                    ConsumeTimeout = timeout,
                    NameOverride = new(),
                }),
        });
    }

    [Test]
    public void ShouldCopyConfiguration()
    {
        var services = new ServiceCollection();

        var fakeConfig = AutoFaker.Generate<NaviConfig>();

        services.AddNavi(nav =>
        {
            nav.MapTopic<TestMessage>("test_topic");

            nav.Suffix = fakeConfig.Suffix;
            nav.Prefix = fakeConfig.Prefix;
            nav.Source = fakeConfig.Source;
            nav.QueueMaxReceiveCount = fakeConfig.QueueMaxReceiveCount;
            nav.RetriesBeforeDeadLetter = fakeConfig.RetriesBeforeDeadLetter;
            nav.PubKey = fakeConfig.PubKey;
            nav.MessageRetentionInDays = fakeConfig.MessageRetentionInDays;
            nav.MessageTimeoutInSeconds = fakeConfig.MessageTimeoutInSeconds;
            nav.MessageDelayInSeconds = fakeConfig.MessageDelayInSeconds;
            nav.RaiseExceptions = fakeConfig.RaiseExceptions;
            nav.MapConsumerEndpoints = fakeConfig.MapConsumerEndpoints;
            nav.PollingIntervalInSeconds = fakeConfig.PollingIntervalInSeconds;
            nav.ServiceUrl = fakeConfig.ServiceUrl;
            nav.Localstack = fakeConfig.Localstack;
            nav.AutoCreateNewTopic = fakeConfig.AutoCreateNewTopic;
            nav.Region = fakeConfig.Region;
            nav.LongPollingWaitInSeconds = fakeConfig.LongPollingWaitInSeconds;
        });

        var sp = services.BuildServiceProvider();
        var config = sp.GetRequiredService<IOptions<NaviConfig>>().Value;
        config.Should().BeEquivalentTo(fakeConfig);
    }

    [Test]
    public void ShouldConfigureLocalStack()
    {
        var services = new ServiceCollection();

        services.AddNavi(nav =>
        {
            nav.MapTopic<TestMessage>("test_topic");
            nav.Source = "app";
            nav.Localstack = true;
        });

        var sp = services.BuildServiceProvider();
        var credentials = sp.GetService<NaviAwsCredentialWrapper>();
        credentials!.Credentials.Should().BeOfType<AnonymousAWSCredentials>();

        var config = sp.GetRequiredService<IOptions<NaviConfig>>().Value;
        config.ServiceUrl.Should().Be("http://localhost:4566");
    }

    [Test]
    public void ShouldConfigureLocalStackWhenConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Navi:Localstack"] = "true",
                    ["Navi:Source"] = "app",
                })
            .Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(_ => configuration!)
            .AddNavi();

        var sp = services.BuildServiceProvider();
        var credentials = sp.GetRequiredService<NaviAwsCredentialWrapper>().Credentials;
        credentials.Should().BeOfType<AnonymousAWSCredentials>();

        var config = sp.GetRequiredService<IOptions<NaviConfig>>().Value;
        config.ServiceUrl.Should().Be("http://localhost:4566");
    }

    [Test]
    public async Task ShouldMapProducersWithCustomPrefix()
    {
        var services = new ServiceCollection();
        var client = A.Fake<IProducerClient>();
        services.AddSingleton(client)
            .AddSingleton(Options.Create(new NaviConfig()));

        var prefix = faker.Random.Word().ToLower();
        var config = new NaviConfigBuilder(services);
        config.MapTopic<TestMessage>("test-topic")
            .WithPrefix(prefix);

        var sp = services.BuildServiceProvider();
        var producer = sp.GetRequiredService<IProducer<TestMessage>>();

        var message = TestMessage.New();
        await producer.TryPublish(message);
        A.CallTo(() => client.Publish(
                "test-topic", message, null,
                A<ProduceOptions>.That.IsEquivalentTo(
                    new ProduceOptions
                    {
                        NameOverride = new(null, prefix),
                    }),
                default))
            .MustHaveHappened();
    }

    [Test]
    public async Task ShouldMapProducersWithCustomSuffix()
    {
        var services = new ServiceCollection();
        var client = A.Fake<IProducerClient>();
        services.AddSingleton(client)
            .AddSingleton(Options.Create(new NaviConfig()));

        var suffix = faker.Random.Word().ToLower();
        var config = new NaviConfigBuilder(services);
        config.MapTopic<TestMessage>("test-topic")
            .WithSuffix(suffix);

        var sp = services.BuildServiceProvider();
        var producer = sp.GetRequiredService<IProducer<TestMessage>>();

        var message = TestMessage.New();
        await producer.TryPublish(message);
        A.CallTo(() => client.Publish(
                "test-topic", message, null,
                A<ProduceOptions>.That.IsEquivalentTo(
                    new ProduceOptions
                    {
                        NameOverride = new(suffix, null),
                    }),
                default))
            .MustHaveHappened();
    }

    [Test]
    public async Task ShouldMapProducersWithCustomSuffixAndPrefix()
    {
        var services = new ServiceCollection();
        var client = A.Fake<IProducerClient>();
        services.AddSingleton(client)
            .AddSingleton(Options.Create(new NaviConfig()));

        var suffix = faker.Random.Word().ToLower();
        var prefix = faker.Random.Word().ToLower();
        var config = new NaviConfigBuilder(services);
        config.MapTopic<TestMessage>("test-topic")
            .WithPrefix(prefix)
            .WithSuffix(suffix);

        var sp = services.BuildServiceProvider();
        var producer = sp.GetRequiredService<IProducer<TestMessage>>();

        var message = TestMessage.New();
        await producer.TryPublish(message);
        A.CallTo(() => client.Publish(
                "test-topic", message, null,
                A<ProduceOptions>.That.IsEquivalentTo(
                    new ProduceOptions
                    {
                        NameOverride = new(suffix, prefix),
                    }),
                default))
            .MustHaveHappened();
    }

    [Test]
    public async Task ShouldMapProducersWithCleanSuffixAndPrefix()
    {
        var services = new ServiceCollection();
        var client = A.Fake<IProducerClient>();
        services.AddSingleton(client)
            .AddSingleton(Options.Create(new NaviConfig()));

        var config = new NaviConfigBuilder(services);
        config.MapTopic<TestMessage>("test-topic")
            .RawTopicName();

        var sp = services.BuildServiceProvider();
        var producer = sp.GetRequiredService<IProducer<TestMessage>>();

        var message = TestMessage.New();
        await producer.TryPublish(message);
        A.CallTo(() => client.Publish(
                "test-topic", message, null,
                A<ProduceOptions>.That.IsEquivalentTo(
                    new ProduceOptions
                    {
                        NameOverride = new("", ""),
                    }),
                default))
            .MustHaveHappened();
    }

    [Test]
    public void ShouldAddProducerDescribers()
    {
        var services = new ServiceCollection();

        services.AddNavi(nav =>
        {
            nav.Source = "source";
            nav.MapTopic<TestMessage>("test_topic")
                .WithPrefix("foo")
                .WithSuffix("bar");
        });

        var sp = services.BuildServiceProvider();
        var describers = sp.GetService<IEnumerable<IProducerDescriber>>();
        describers.Should().BeEquivalentTo(new[]
        {
            new ProducerDescriber(
                "test_topic",
                typeof(TestMessage),
                new TopicNameOverride
                {
                    Prefix = "foo",
                    Suffix = "bar",
                }),
        });
    }
}
