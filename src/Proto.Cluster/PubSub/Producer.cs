// -----------------------------------------------------------------------
// <copyright file="Publisher.cs" company="Asynkron AB">
//      Copyright (C) 2015-2022 Asynkron AB All rights reserved
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Proto.Utils;

namespace Proto.Cluster.PubSub;

public record ProduceMessage(object Message, TaskCompletionSource<bool> TaskCompletionSource);

[PublicAPI]
public class Producer : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.CreateLogger<Producer>();
    private static readonly ShouldThrottle _logThrottle = Throttle.Create(3, TimeSpan.FromSeconds(10));

    private readonly Func<ProducerBatchMessage, Task> _requestToTopic;
    private readonly string _topic = string.Empty;

    private readonly Channel<ProduceMessage> _publisherChannel;
    private readonly int _batchSize;
    private CancellationTokenSource _cts = new();
    private Task _publisherLoop;

    public Producer(Cluster cluster, string topic, int? maxQueueSize = null)
        : this(
            batch => cluster.RequestAsync<PublishResponse>(topic, TopicActor.Kind, batch, CancellationTokens.FromSeconds(5)),
            cluster.Config.PubSubBatchSize,
            maxQueueSize
        ) => _topic = topic;

    internal Producer(Func<ProducerBatchMessage, Task> requestToTopic, int batchSize, int? maxQueueSize = null)
    {
        _requestToTopic = requestToTopic;
        _batchSize = batchSize;

        _publisherChannel = maxQueueSize != null
            ? Channel.CreateBounded<ProduceMessage>(maxQueueSize.Value)
            : Channel.CreateUnbounded<ProduceMessage>();

        _publisherLoop = Task.Run(() => PublisherLoop(_cts.Token));
    }

    private async Task PublisherLoop(CancellationToken cancel)
    {
        Logger.LogDebug("Producer is starting the publisher loop for topic {Topic}", _topic);

        var batch = new ProducerBatchMessage();

        try
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    if (_publisherChannel.Reader.TryRead(out var produceMessage))
                    {
                        var message = produceMessage.Message;
                        var taskCompletionSource = produceMessage.TaskCompletionSource;
                        batch.Envelopes.Add(message);
                        batch.DeliveryReports.Add(taskCompletionSource);

                        if (batch.Envelopes.Count < _batchSize) continue;

                        await PublishBatch(batch);
                        batch = new ProducerBatchMessage();
                    }
                    else
                    {
                        if (batch.Envelopes.Count > 0)
                        {
                            await PublishBatch(batch);
                            batch = new ProducerBatchMessage();
                        }

                        await _publisherChannel.Reader.WaitToReadAsync(cancel);
                    }
                }
            }
            finally
            {
                // at this point stop accepting new messages
                _publisherChannel.Writer.Complete();
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // expected, disposing
        }
        catch (Exception e)
        {
            if (_logThrottle().IsOpen())
                Logger.LogError(e, "Error in the publisher loop of Producer for topic {Topic}", _topic);

            PurgeCurrentBatch(batch, e);
            await PurgePendingMessages(e);
        }

        PurgeCurrentBatch(batch);
        await PurgePendingMessages();

        Logger.LogDebug("Producer is stopping the publisher loop for topic {Topic}", _topic);
    }

    private async Task PurgePendingMessages(Exception? ex = null)
    {
        await foreach (var producerMessage in _publisherChannel.Reader.ReadAllAsync())
        {
            if (ex != null)
                producerMessage.TaskCompletionSource.SetException(ex);
            else
                producerMessage.TaskCompletionSource.SetCanceled();
        }
    }

    private void PurgeCurrentBatch(ProducerBatchMessage batch, Exception? ex = null)
    {
        foreach (var deliveryReport in batch.DeliveryReports)
        {
            if (ex != null)
                deliveryReport.SetException(ex);
            else
                deliveryReport.SetCanceled();
        }

        batch.Envelopes.Clear();
        batch.DeliveryReports.Clear();
    }

    private async Task PublishBatch(ProducerBatchMessage batch)
    {
        //TODO: retries etc...
        await _requestToTopic(batch);

        foreach (var tcs in batch.DeliveryReports)
        {
            tcs.SetResult(true);
        }
    }

    public Task ProduceAsync(object message)
    {
        var tcs = new TaskCompletionSource<bool>();

        if (!_publisherChannel.Writer.TryWrite(new ProduceMessage(message, tcs)))
        {
            if(_publisherChannel.Reader.Completion.IsCompleted)
                throw new InvalidOperationException($"This producer for topic {_topic} is stopped, cannot produce more messages.");

            throw new ProducerQueueFullException(_topic);
        }

        return tcs.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _publisherLoop;
        _cts.Dispose();
    }
}

public class ProducerQueueFullException : Exception {
    public ProducerQueueFullException(string topic) : base($"Producer for topic {topic} has full queue") { }
}