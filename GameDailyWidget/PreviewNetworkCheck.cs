using System;
using System.Diagnostics.Tracing;
using System.Threading;

namespace GameDailyWidget;

// Enabled only for selected offline commands. The runtime delivers provider events for this
// process. Count starts, including failed attempts, without inspecting addresses or payloads.
internal sealed class PreviewNetworkCheck : EventListener
{
    private const string Sockets = "System.Net.Sockets";
    private const string Dns = "System.Net.NameResolution";
    private int _socketStarts;
    private int _dnsStarts;

    internal readonly record struct Counts(int SocketConnectStarts, int DnsResolutionStarts)
    {
        public bool IsOffline => SocketConnectStarts == 0 && DnsResolutionStarts == 0;
    }

    protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
    {
        if (source.Name is Sockets or Dns)
            EnableEvents(source, EventLevel.Informational, EventKeywords.All);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        switch (e.EventSource.Name, e.EventName)
        {
            case (Sockets, "ConnectStart"):
                Interlocked.Increment(ref _socketStarts);
                break;
            case (Dns, "ResolutionStart"):
                Interlocked.Increment(ref _dnsStarts);
                break;
        }
    }

    internal Counts Snapshot() => new(Volatile.Read(ref _socketStarts), Volatile.Read(ref _dnsStarts));

    internal string Result()
    {
        var counts = Snapshot();
        return $"{(counts.IsOffline ? "PASS" : "FAIL")} 托管网络观测 · 套接字连接开始 {counts.SocketConnectStarts} · DNS 解析开始 {counts.DnsResolutionStarts}";
    }

    internal void AssertOffline()
    {
        if (!Snapshot().IsOffline)
            throw new InvalidOperationException(Result());
    }
}
