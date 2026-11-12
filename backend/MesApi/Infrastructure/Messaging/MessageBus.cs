using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MesApi.Infrastructure.Messaging;

/// <summary>
/// 领域事件：一台 SN 完成一次过站。看板推送、后续的良率实时计算都从这一条事件派生。
/// 用 record + JSON 走 RabbitMQ，消息体积小（<1KB），200 终端高峰也没压力。
/// </summary>
public record ScanReceivedEvent(
    string Sn,
    string OrderNo,
    string StepCode,
    string StepName,
    string Result,
    string OperatorName,
    string StationCode,
    DateTime AtUtc,
    string ProductionDate);

/// <summary>
/// 事件总线门面：配置了 RabbitMQ 就走 MQ（解耦、可重放、跨服务），否则降级为进程内直投。
/// 业务代码只管 PublishAsync，感知不到后端差异。
/// </summary>
public interface IMessageBus
{
    Task PublishScanReceivedAsync(ScanReceivedEvent evt, CancellationToken ct = default);

    /// <summary>订阅扫码事件。返回取消订阅用的 disposable（HostedService 停机时释放）。</summary>
    IDisposable SubscribeScanReceived(Func<ScanReceivedEvent, CancellationToken, Task> handler);

    string Backend { get; }
}

/// <summary>进程内实现：演示模式 / RabbitMQ 故障时的兜底，事件直接在内存里转发。</summary>
public sealed class InMemoryMessageBus : IMessageBus
{
    private readonly List<Func<ScanReceivedEvent, CancellationToken, Task>> _handlers = [];
    private readonly object _lock = new();

    public string Backend => "in-memory";

    public Task PublishScanReceivedAsync(ScanReceivedEvent evt, CancellationToken ct = default)
    {
        Func<ScanReceivedEvent, CancellationToken, Task>[] snapshot;
        lock (_lock) snapshot = [.. _handlers];
        return Task.WhenAll(snapshot.Select(h => SafeInvokeAsync(h, evt, ct)));
    }

    public IDisposable SubscribeScanReceived(Func<ScanReceivedEvent, CancellationToken, Task> handler)
    {
        lock (_lock) _handlers.Add(handler);
        return new Unsubscriber(() => { lock (_lock) _handlers.Remove(handler); });
    }

    private static async Task SafeInvokeAsync(Func<ScanReceivedEvent, CancellationToken, Task> handler, ScanReceivedEvent evt, CancellationToken ct)
    {
        try { await handler(evt, ct); }
        catch { /* 单个订阅者失败不影响其它订阅者 */ }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>
/// RabbitMQ 实现：topic exchange "mes.events"，routing key "product.scanned"。
/// 断线自动恢复（AutomaticRecoveryEnabled）；发送失败降级进程内直投，保证看板不断流。
/// </summary>
public sealed class RabbitMqMessageBus(
    IConfiguration config,
    ILogger<RabbitMqMessageBus> logger,
    InMemoryMessageBus fallback) : IMessageBus, IAsyncDisposable
{
    private const string ExchangeName = "mes.events";
    private const string RoutingKey = "product.scanned";
    private const string QueueName = "mes.scan-dashboard";

    private IConnection? _connection;
    private IChannel? _channel;
    private DateTime _lastRetryAt = DateTime.MinValue;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public string Backend => _channel is not null ? "rabbitmq" : "in-memory";

    private async Task<IChannel?> TryGetChannelAsync(CancellationToken ct)
    {
        if (_channel is not null) return _channel;
        if (DateTime.UtcNow - _lastRetryAt < TimeSpan.FromSeconds(30)) return null;
        await _connectLock.WaitAsync(ct);
        try
        {
            if (_channel is not null) return _channel;
            if (DateTime.UtcNow - _lastRetryAt < TimeSpan.FromSeconds(30)) return null;
            _lastRetryAt = DateTime.UtcNow;

            var factory = new ConnectionFactory
            {
                HostName = config["RabbitMq:Host"] ?? "localhost",
                Port = config.GetValue("RabbitMq:Port", 5672),
                UserName = config["RabbitMq:UserName"] ?? "mes",
                Password = config["RabbitMq:Password"] ?? "",
                VirtualHost = config["RabbitMq:VirtualHost"] ?? "/",
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            };

            _connection = await factory.CreateConnectionAsync(ct);
            _channel = await _connection.CreateChannelAsync(cancellationToken: ct);
            await _channel.ExchangeDeclareAsync(ExchangeName, ExchangeType.Topic, durable: true, cancellationToken: ct);
            await _channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
            await _channel.QueueBindAsync(QueueName, ExchangeName, RoutingKey, cancellationToken: ct);
            logger.LogInformation("RabbitMQ 已连接，事件总线切换到 rabbitmq 后端");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RabbitMQ 连接失败，降级进程内事件（30 秒后重试）");
            _channel = null;
        }
        finally
        {
            _connectLock.Release();
        }
        return _channel;
    }

    public async Task PublishScanReceivedAsync(ScanReceivedEvent evt, CancellationToken ct = default)
    {
        var channel = await TryGetChannelAsync(ct);
        if (channel is null)
        {
            await fallback.PublishScanReceivedAsync(evt, ct);
            return;
        }

        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(evt);
            var props = new BasicProperties { ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent };
            await channel.BasicPublishAsync(ExchangeName, RoutingKey, mandatory: false, props, body, ct);
        }
        catch (Exception ex)
        {
            // 消息丢了看板就断流，宁可降级也不吞掉
            logger.LogWarning(ex, "RabbitMQ 发布失败，降级进程内直投");
            await fallback.PublishScanReceivedAsync(evt, ct);
        }
    }

    public IDisposable SubscribeScanReceived(Func<ScanReceivedEvent, CancellationToken, Task> handler)
    {
        // 进程内订阅者：MQ 可用时走后台消费循环；MQ 不可用时由 fallback 直接调
        StartConsumerLoopIfAvailable(handler);
        return fallback.SubscribeScanReceived(handler);
    }

    private void StartConsumerLoopIfAvailable(Func<ScanReceivedEvent, CancellationToken, Task> handler)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var channel = await TryGetChannelAsync(CancellationToken.None);
                if (channel is null)
                {
                    // MQ 没起来：挂在这等 30 秒再试。fallback 订阅已在内存里兜底，消息不丢
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    continue;
                }

                try
                {
                    var consumer = new AsyncEventingBasicConsumer(channel);
                    consumer.ReceivedAsync += async (_, ea) =>
                    {
                        try
                        {
                            var evt = JsonSerializer.Deserialize<ScanReceivedEvent>(ea.Body.Span);
                            if (evt is not null) await handler(evt, CancellationToken.None);
                            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                        }
                        catch
                        {
                            // 反序列化失败也 ack 掉：脏消息重投三次只会变成毒消息
                            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                        }
                    };
                    await channel.BasicConsumeAsync(QueueName, autoAck: false, consumer, CancellationToken.None);
                    return; // 消费循环建立成功，重连交给 AutomaticRecovery
                }
                catch
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        _connectLock.Dispose();
    }
}
