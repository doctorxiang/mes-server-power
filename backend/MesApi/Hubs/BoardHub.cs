using Microsoft.AspNetCore.SignalR;

namespace MesApi.Hubs;

/// <summary>一次扫码事件（推送给看板，体积尽量小，200 终端同时在线也不吃力）。</summary>
public record BoardScanEvent(
    string Sn,
    string OrderNo,
    string StepCode,
    string StepName,
    string Result,
    string OperatorName,
    string StationCode,
    DateTime AtUtc,
    string ProductionDate);

public class BoardHub : Hub
{
    /// <summary>看板客户端订阅某条产线（lineId=0 表示全厂）。</summary>
    public Task Subscribe(int lineId) => Groups.AddToGroupAsync(Context.ConnectionId, $"line:{lineId}");

    public Task Unsubscribe(int lineId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"line:{lineId}");
}

public interface IBoardNotifier
{
    Task ScanAsync(BoardScanEvent evt, CancellationToken ct);
}

/// <summary>扫码事件实时广播：看板无需轮询即可刷新过站流水。</summary>
public class BoardNotifier(IHubContext<BoardHub> hub) : IBoardNotifier
{
    public Task ScanAsync(BoardScanEvent evt, CancellationToken ct)
        => hub.Clients.All.SendAsync("scan", evt, ct);
}
