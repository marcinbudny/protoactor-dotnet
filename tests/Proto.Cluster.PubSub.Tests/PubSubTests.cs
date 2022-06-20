﻿// -----------------------------------------------------------------------
// <copyright file = "PubSubTests.cs" company = "Asynkron AB">
//      Copyright (C) 2015-2022 Asynkron AB All rights reserved
// </copyright>
// ----------------------------------------------------------------------- 
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Proto.Cluster.Tests;
using Xunit;
using Xunit.Abstractions;

namespace Proto.Cluster.PubSub.Tests;

public class PubSubTests : IClassFixture<PubSubTests.PubSubInMemoryClusterFixture>
{
    private const string SubscriberKind = "Subscriber";

    private readonly PubSubInMemoryClusterFixture _fixture;
    private readonly ITestOutputHelper _output;

    public PubSubTests(PubSubInMemoryClusterFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.Output = output;
        _output = output;
        _fixture.Deliveries.Clear();
    }

    [Fact]
    public async Task Can_deliver_single_messages()
    {
        var subscriberIds = SubscriberIds("single-test", 20);
        const string topic = "single-test-topic";
        const int numMessages = 100;

        await SubscribeAllTo(topic, subscriberIds);

        for (var i = 0; i < numMessages; i++)
        {
            var response = await PublishData(topic, i);
            if (response == null)
                await _fixture.Members.DumpClusterState(_output);
            response.Should().NotBeNull("publishing should not time out");
        }

        await UnsubscribeAllFrom(topic, subscriberIds);

        VerifyAllSubscribersGotAllTheData(subscriberIds, numMessages);
    }

    [Fact]
    public async Task Can_deliver_message_batches()
    {
        var subscriberIds = SubscriberIds("batch-test", 20);
        const string topic = "batch-test-topic";
        const int numMessages = 100;

        await SubscribeAllTo(topic, subscriberIds);

        for (var i = 0; i < numMessages / 10; i++)
        {
            var data = Enumerable.Range(i * 10, 10).ToArray();
            var response = await PublishDataBatch(topic, data);
            if (response == null)
                await _fixture.Members.DumpClusterState(_output);
            response.Should().NotBeNull("publishing should not time out");
        }

        await UnsubscribeAllFrom(topic, subscriberIds);

        VerifyAllSubscribersGotAllTheData(subscriberIds, numMessages);
    }

    [Fact]
    public async Task Unsubscribed_actor_does_not_receive_messages()
    {
        const string sub1 = "unsubscribe-test-1";
        const string sub2 = "unsubscribe-test-2";
        const string topic = "unsubscribe-test";

        await SubscribeTo(topic, sub1);
        await SubscribeTo(topic, sub2);

        await UnsubscribeFrom(topic, sub2);

        await PublishData(topic, 1);

        _fixture.Deliveries.Should().HaveCount(1, "only one delivery should happen because the other actor is unsubscribed");
        _fixture.Deliveries.First().Identity.Should().Be(sub1, "the other actor should be unsubscribed");
    }

    [Fact]
    public async Task Can_subscribe_with_PID()
    {
        const string topic = "pid-subscribe";

        DataPublished? deliveredMessage = null;

        var props = Props.FromFunc(ctx => {
                if (ctx.Message is DataPublished d) deliveredMessage = d;
                return Task.CompletedTask;
            }
        );

        var member = _fixture.Members.First();
        var pid = member.System.Root.Spawn(props);
        await member.Subscribe(topic, pid);

        await PublishData(topic, 1);

        await member.Unsubscribe(topic, pid);

        deliveredMessage.Should().BeEquivalentTo(new DataPublished(1));
    }

    [Fact]
    public async Task Can_unsubscribe_with_PID()
    {
        const string topic = "pid-unsubscribe";

        var deliveryCount = 0;

        var props = Props.FromFunc(ctx => {
                if (ctx.Message is DataPublished) Interlocked.Increment(ref deliveryCount);
                return Task.CompletedTask;
            }
        );

        var member = _fixture.Members.First();
        var pid = member.System.Root.Spawn(props);

        await member.Subscribe(topic, pid);
        await member.Unsubscribe(topic, pid);

        await PublishData(topic, 1);

        deliveryCount.Should().Be(0);
    }

    [Fact]
    public async Task Stopped_actor_that_did_not_unsubscribe_does_not_block_publishing_to_topic()
    {
        const string topic = "missing-unsubscribe";

        var deliveryCount = 0;

        // this scenario is only relevant for regular actors,
        // virtual actors always exist, so the msgs should never be deadlettered 
        var props = Props.FromFunc(ctx => {
                if (ctx.Message is DataPublished) Interlocked.Increment(ref deliveryCount);
                return Task.CompletedTask;
            }
        );

        // spawn two actors and subscribe them to the topic
        var member = _fixture.Members.First();
        var pid1 = member.System.Root.Spawn(props);
        var pid2 = member.System.Root.Spawn(props);

        await member.Subscribe(topic, pid1);
        await member.Subscribe(topic, pid2);

        // publish one message
        await PublishData(topic, 1);

        // kill one of the actors
        await member.System.Root.StopAsync(pid2);

        // publish again
        var response = await PublishData(topic, 2);

        // clean up and assert
        await member.Unsubscribe(topic, pid1, CancellationToken.None);

        response.Should().NotBeNull("the publish operation shouldn't have timed out");
        deliveryCount.Should().Be(3, "second publish should be delivered only to one of the actors");
    }

    [Fact]
    [SuppressMessage("ReSharper", "AccessToDisposedClosure")]
    public async Task Can_publish_messages_via_batching_producer()
    {
        var subscriberIds = SubscriberIds("batching-producer-test", 20);
        const string topic = "batching-producer";
        const int numMessages = 100;

        await SubscribeAllTo(topic, subscriberIds);

        await using var producer = _fixture.Members.First().BatchingProducer(topic, new BatchingProducerConfig {BatchSize = 10});

        var tasks = Enumerable.Range(0, numMessages).Select(i => producer.ProduceAsync(new DataPublished(i)));
        await Task.WhenAll(tasks);

        await UnsubscribeAllFrom(topic, subscriberIds);

        VerifyAllSubscribersGotAllTheData(subscriberIds, numMessages);
    }

    private void VerifyAllSubscribersGotAllTheData(string[] subscriberIds, int numMessages)
    {
        var expected = subscriberIds
            .SelectMany(id => Enumerable.Range(0, numMessages).Select(i => new Delivery(id, i)))
            .ToArray();

        var actual = _fixture.Deliveries.OrderBy(d => d.Identity).ThenBy(d => d.Data).ToArray();

        try
        {
            actual.Should().Equal(expected, "the data published should be received by all subscribers");
        }
        catch
        {
            _output.WriteLine(actual
                .GroupBy(d => d.Identity)
                .Select(g => (g.Key, Data: g.Aggregate("", (acc, delivery) => acc + delivery.Data + ",")))
                .Aggregate("", (acc, d) => $"{acc}ID: {d.Key}, got: {d.Data}\n")
            );

            throw;
        }
    }

    private async Task SubscribeAllTo(string topic, string[] subscriberIds)
    {
        foreach (var id in subscriberIds)
        {
            await SubscribeTo(topic, id);
        }
    }

    private async Task UnsubscribeAllFrom(string topic, string[] subscriberIds)
    {
        foreach (var id in subscriberIds)
        {
            await UnsubscribeFrom(topic, id);
        }
    }

    private string[] SubscriberIds(string prefix, int count) => Enumerable.Range(1, count).Select(i => $"{prefix}-{i:D4}").ToArray();

    private Task SubscribeTo(string topic, string identity, string kind = SubscriberKind)
        => RequestViaRandomMember(identity, new Subscribe(topic), kind);

    private Task UnsubscribeFrom(string topic, string identity, string kind = SubscriberKind)
        => RequestViaRandomMember(identity, new Unsubscribe(topic), kind);

    private Task<PublishResponse?> PublishData(string topic, int data) => PublishViaRandomMember(topic, new DataPublished(data));

    private Task<PublishResponse?> PublishDataBatch(string topic, int[] data)
        => PublishViaRandomMember(topic, data.Select(d => new DataPublished(d)).ToArray());

    private readonly Random _random = new();

    private async Task<Response?> RequestViaRandomMember(string identity, object message, string kind = SubscriberKind)
    {
        var response = await _fixture
            .Members[_random.Next(_fixture.Members.Count)]
            .RequestAsync<Response?>(identity, kind, message, CancellationTokens.FromSeconds(1));

        if (response == null)
            await _fixture.Members.DumpClusterState(_output);

        response.Should().NotBeNull($"request {message.GetType().Name} should time out");

        return response;
    }

    private Task<PublishResponse?> PublishViaRandomMember(string topic, object message) =>
        _fixture
            .Members[_random.Next(_fixture.Members.Count)]
            .Publisher()
            .Publish(topic, message, CancellationTokens.FromSeconds(1));

    private Task<PublishResponse> PublishViaRandomMember<T>(string topic, T[] messages) =>
        _fixture
            .Members[_random.Next(_fixture.Members.Count)]
            .Publisher()
            .PublishBatch(topic, messages, CancellationTokens.FromSeconds(1));

    private record DataPublished(int Data);

    public record Delivery(string Identity, int Data);

    private record Subscribe(string Topic);

    private record Unsubscribe(string Topic);

    private record Response;

    public class PubSubInMemoryClusterFixture : BaseInMemoryClusterFixture
    {
        public ConcurrentBag<Delivery> Deliveries = new();
        public ITestOutputHelper? Output;

        public PubSubInMemoryClusterFixture() : base(3)
        {
        }

        protected override ClusterKind[] ClusterKinds => new[]
        {
            new ClusterKind(SubscriberKind, SubscriberProps())
        };

        private Props SubscriberProps()
        {
            async Task Receive(IContext context)
            {
                switch (context.Message)
                {
                    case DataPublished msg:
                        Deliveries.Add(new Delivery(context.ClusterIdentity()!.Identity, msg.Data));
                        context.Respond(new Response());
                        break;

                    case Subscribe msg:
                        var subRes = await context.Cluster().Subscribe(msg.Topic, context.ClusterIdentity()!);
                        if (subRes == null)
                            Output?.WriteLine($"{context.ClusterIdentity()!.Identity} failed to subscribe due to timeout");

                        context.Respond(new Response());
                        break;

                    case Unsubscribe msg:
                        var unsubRes = await context.Cluster().Unsubscribe(msg.Topic, context.ClusterIdentity()!);
                        if (unsubRes == null)
                            Output?.WriteLine($"{context.ClusterIdentity()!.Identity} failed to subscribe due to timeout");

                        context.Respond(new Response());
                        break;
                }
            }

            return Props.FromFunc(Receive);
        }
    }
}