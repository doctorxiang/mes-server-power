using MesApi.Hubs;

namespace MesApi.Infrastructure.Messaging;

/// <summary>
/// 扫码事件订阅者：把事件总线上的 product.scanned 转发到 SignalR 看板。
/// 这是「业务服务发消息 → 消费者推看板」解耦的关键一环：
/// 高峰期就算看板推送卡住，过站主流程也不受影响。
/// </summary>
public sealed class ScanEventDispatcher(IBoardNotifier notifier) : IDisposable
{
    private IDisposable? _subscription;

    public void Attach(IMessageBus bus)
    {
        _subscription ??= bus.SubscribeScanReceived((evt, ct) => notifier.ScanAsync(
            new BoardScanEvent(evt.Sn, evt.OrderNo, evt.StepCode, evt.StepName,
                evt.Result, evt.OperatorName, evt.StationCode, evt.AtUtc, evt.ProductionDate), ct));
    }

    public void Dispose() => _subscription?.Dispose();
}

/// <summary>启动时把分发器挂到事件总线上。</summary>
public sealed class MessageBusBootstrap(IMessageBus bus, ScanEventDispatcher dispatcher) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        dispatcher.Attach(bus);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
