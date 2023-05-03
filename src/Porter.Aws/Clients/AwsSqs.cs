using System.Collections.Concurrent;
using System.Runtime.Serialization;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Porter.Extensions;
using Porter.Models;
using Porter.Services;

namespace Porter.Clients;

readonly record struct QueueInfo(Uri Url, SqsArn Arn, TimeSpan VisibilityTimeout);

interface IConsumeDriver : IDisposable
{
    Task<IReadOnlyCollection<IMessage>> ReceiveMessages(TopicId topic, CancellationToken ctx);
    Task<IReadOnlyCollection<IMessage>> ReceiveDeadLetters(TopicId topic, CancellationToken ctx);
}

sealed class AwsSqs : IConsumeDriver
{
    const string DeadLetterPrefix = "dead_letter_";
    static readonly ConcurrentDictionary<string, QueueInfo> queueUrlCache = new();

    public static readonly string Iam = JsonSerializer.Serialize(new
    {
        Id = "SQSEventsPolicy",
        Version = "2012-10-17",
        Statement = new[]
        {
            new
            {
                Sid = "Allow_SQS_Services",
                Action = "sqs:*",
                Effect = "Allow",
                Resource = "arn:aws:sqs:*",
                Principal = new
                {
                    AWS = "*",
                },
            },
        },
    });

    readonly PorterConfig config;
    readonly AwsKms kms;
    readonly ILogger<AwsSqs> logger;
    readonly IPorterMessageSerializer serializer;
    readonly TagsService tags;
    readonly IDiagnostics diagnostics;
    readonly IAmazonSQS sqs;

    public AwsSqs(
        ILogger<AwsSqs> logger,
        IOptions<PorterConfig> config,
        IPorterMessageSerializer serializer,
        TagsService tags,
        IDiagnostics diagnostics,
        IAmazonSQS sqs,
        AwsKms kms)
    {
        this.sqs = sqs;
        this.kms = kms;
        this.logger = logger;
        this.serializer = serializer;
        this.tags = tags;
        this.diagnostics = diagnostics;
        this.config = config.Value;
    }

    public async Task<QueueInfo> GetQueueAttributes(string queueUrl, CancellationToken ctx)
    {
        var response = await sqs.GetQueueAttributesAsync(queueUrl, new List<string>
            {
                QueueAttributeName.QueueArn,
                QueueAttributeName.VisibilityTimeout,
            }, ctx)
            .ConfigureAwait(false);

        logger.LogDebug("Queue Attributes Response is: {Response}",
            JsonSerializer.Serialize(response.Attributes));

        return new(
            new(queueUrl),
            new(response.QueueARN),
            TimeSpan.FromSeconds(response.VisibilityTimeout)
        );
    }

    public void ClearCache() => queueUrlCache.Clear();

    public async ValueTask<QueueInfo?> GetQueue(string queueName,
        CancellationToken ctx, bool deadLetter = false)
    {
        var queue = $"{(deadLetter ? "dead_letter_" : string.Empty)}{queueName}";

        if (queueUrlCache.TryGetValue(queue, out var cachedInfo))
            return cachedInfo;

        var responseQueues =
            await sqs.ListQueuesAsync(new ListQueuesRequest
            {
                QueueNamePrefix = queue,
                MaxResults = 1000,
            }, ctx)
                .ConfigureAwait(false);

        var url = responseQueues.QueueUrls.Find(name => name.Contains(queue));
        if (url is null) return null;

        var info = await GetQueueAttributes(url, ctx);
        queueUrlCache.AddOrUpdate(queue, info, (_, _) => info);
        return info;
    }

    public async Task<bool> QueueExists(string queueName, CancellationToken ctx,
        bool deadLetter = false) =>
        await GetQueue(queueName, ctx, deadLetter) is not null;

    public async Task<QueueInfo> CreateQueue(string queueName, CancellationToken ctx)
    {
        logger.LogInformation("Creating queue: {Name}", queueName);
        var keyId = await kms.GetKey(ctx) ??
                    throw new InvalidOperationException("Default KMS EncryptionKey Id not found");

        var deadLetter = await CreateDeadLetterQueue(queueName, keyId.Value, ctx);

        var deadLetterPolicy = new
        {
            deadLetterTargetArn = deadLetter.Arn.Value,
            maxReceiveCount = config.RetriesBeforeDeadLetter.ToString(),
        };

        var createQueueRequest = new CreateQueueRequest
        {
            QueueName = queueName,
            Tags = tags.GetTags(),
            Attributes = new()
            {
                [QueueAttributeName.RedrivePolicy] = JsonSerializer.Serialize(deadLetterPolicy),
                [QueueAttributeName.Policy] = Iam,
                [QueueAttributeName.KmsMasterKeyId] = keyId.Value,
                [QueueAttributeName.VisibilityTimeout] = config.MessageTimeoutInSeconds.ToString(),
                [QueueAttributeName.DelaySeconds] = config.MessageDelayInSeconds.ToString(),
                [QueueAttributeName.MessageRetentionPeriod] =
                    $"{(int)TimeSpan.FromDays(config.MessageRetentionInDays).TotalSeconds}",
            },
        };
        var q = await sqs.CreateQueueAsync(createQueueRequest, ctx)
            .ConfigureAwait(false);

        return await GetQueueAttributes(q.QueueUrl, ctx);
    }

    public async Task UpdateQueueAttributes(
        string queueName,
        TimeSpan visibilityTimeout,
        CancellationToken ctx = default)
    {
        var queue = await GetQueue(queueName, ctx);
        if (queue is null)
            return;

        var newTimeout = (int)visibilityTimeout.TotalSeconds;

        var attrs = new Dictionary<string, string>();
        if (queue.Value.VisibilityTimeout != visibilityTimeout)
            attrs.Add(QueueAttributeName.VisibilityTimeout,
                newTimeout.ToString());

        if (attrs.Count == 0)
            return;

        logger.LogInformation("Updating queue {QueueName} attributes", queueName);
        await sqs.SetQueueAttributesAsync(queue.Value.Url.ToString(), attrs, ctx);

        var info = queue.Value with
        {
            VisibilityTimeout = visibilityTimeout,
        };
        queueUrlCache.AddOrUpdate(queueName, info, (_, _) => info);
    }

    async Task<QueueInfo> CreateDeadLetterQueue(string queueName, string keyId,
        CancellationToken ctx)
    {
        logger.LogInformation("Creating dead letter queue: {Name}", queueName);
        var q = await sqs.CreateQueueAsync(
                new CreateQueueRequest
                {
                    QueueName = $"{DeadLetterPrefix}{queueName}",
                    Attributes = new()
                    {
                        ["Policy"] = Iam,
                        ["KmsMasterKeyId"] = keyId,
                    },
                }, ctx)
            .ConfigureAwait(false);
        return await GetQueueAttributes(q.QueueUrl, ctx);
    }

    async Task<IReadOnlyCollection<IMessage<string>>> ReceiveMessages(
        TopicId topic,
        bool deadLetter,
        CancellationToken ctx)
    {
        var queue = deadLetter ? $"{DeadLetterPrefix}{topic.QueueName}" : topic.QueueName;

        if (await GetQueue(queue, ctx) is not { } queueInfo)
            throw new InvalidOperationException($"Unable to get '{queue}' data");

        var queueUrl = queueInfo.Url.ToString();
        var receiveMessageResponse = await sqs.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = config.QueueMaxReceiveCount,
                    WaitTimeSeconds = config.LongPollingWaitInSeconds,
                    AttributeNames = new()
                    {
                        MessageSystemAttributeName.ApproximateReceiveCount,
                    },
                }, ctx)
            .ConfigureAwait(false);

        if (receiveMessageResponse?.Messages is not { Count: > 0 } messages)
            return Array.Empty<IMessage<string>>();

        var parsedMessages = messages
            .Select(sqsMessage =>
            {
                using var activity = diagnostics.StartConsumerActivity(topic.TopicName);

                try
                {
                    logger.LogDebug("Received {Queue} Raw: {Body}", queue, sqsMessage.Body);

                    var payload =
                        JsonSerializer.Deserialize<SnsEnvelope>(sqsMessage.Body.EncodeAsUtf8()) ??
                        throw new SerializationException("Unable to deserialize message");

                    logger.LogDebug("Received {Queue} Payload: {Message}", queue,
                        payload.Message);

                    var envelope = serializer.Deserialize<MessageEnvelope>(payload.Message);
                    var message = envelope.Payload;

                    diagnostics.SetActivityMessageAttributes(
                        activity,
                        queueUrl,
                        envelope.MessageId,
                        envelope.CorrelationId,
                        payload.Message);

                    logger.LogDebug("Received {Queue} Message: {Message}", queue,
                        message.RootElement.ToString());

                    var receivedCount =
                        sqsMessage.Attributes
                            .TryGetValue(MessageSystemAttributeName.ApproximateReceiveCount,
                                out var receiveString) &&
                        receiveString is not null &&
                        uint.TryParse(receiveString, out var received)
                            ? received
                            : 0;

                    var jsonBody = message.RootElement.GetRawText();
                    var parsedMessage =
                        new SqsMessage<string>(jsonBody, sqsMessage.ReceiptHandle, sqs)
                        {
                            MessageId = envelope.MessageId ?? payload.MessageId,
                            Datetime = envelope.DateTime,
                            CorrelationId = envelope.CorrelationId,
                            QueueUrl = queueUrl,
                            TopicArn = payload.TopicArn,
                            RetryNumber = receivedCount - 1,
                        };

                    return parsedMessage;
                }
                catch (Exception e)
                {
                    logger.LogCritical(e, "Bad Message: {Body}", sqsMessage.Body);
                    throw;
                }
            })
            .Cast<IMessage>()
            .ToArray();

        diagnostics.AddRetrievedMessages(parsedMessages.Length, topic.RawName);

        return parsedMessages;
    }

    public Task<IReadOnlyCollection<IMessage>> ReceiveDeadLetters(TopicId topic,
        CancellationToken ctx) =>
        ReceiveMessages(topic, true, ctx);

    public Task<IReadOnlyCollection<IMessage>>
        ReceiveMessages(TopicId topic, CancellationToken ctx) =>
        ReceiveMessages(topic, false, ctx);

    internal class SnsEnvelope
    {
        public required Guid MessageId { get; set; }
        public required string Message { get; set; }

        public required string? TopicArn { get; set; }
    }

    public void Dispose() => sqs.Dispose();
}
