using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CodexTokenOverlay;

internal static class DebugRouteTests
{
    internal static void Run(Action<string, bool> check)
    {
        const string a = "11111111-1111-4111-8111-111111111111";
        const string b = "22222222-2222-4222-8222-222222222222";
        check("local MemoryRouter path identifies exact thread", CodexDebugRouteReader.ThreadFromPath("/local/" + a) == a);
        check("home clears exact thread", CodexDebugRouteReader.ThreadFromPath("/") is null);
        check("remote host route is not treated as local", CodexDebugRouteReader.ThreadFromPath("/remote/" + a) is null);
        check("trailing segments cannot alias local conversation", CodexDebugRouteReader.ThreadFromPath("/local/" + a + "/other") is null);
        check("malformed thread ID is rejected", CodexDebugRouteReader.ThreadFromPath("/local/not-a-thread") is null);
        check("legacy route accepts exact GUID", CodexDebugRouteReader.ThreadFromPath("/threads/" + b) == b);
        static string Route(string path, string key) => JsonSerializer.Serialize(new { path, key });
        using var server = new SyntheticCdpPage();
        using var reader = new CodexDebugRouteReader();
        server.Payload = Route("/local/" + a, "a");
        var first = reader.ReadProcess(Environment.ProcessId, "test-owner", default);
        check("owned loopback CDP transport reads current route", first?.ThreadId == a && first.IsConnected);
        check("route source does not depend on title or sidebar", first?.Identification == "页面唯一 ID");
        server.Payload = Route("/local/" + b, "b");
        var second = reader.ReadProcess(Environment.ProcessId, "test-owner", default);
        check("same-title conversation switches by unique ID", second?.ThreadId == b);
        check("switch changes publication generation", first is not null && second is not null && CodexVisibleThreadMonitor.Advance(first, second).Version > first.Version);
        server.Payload = Route("/local/" + b, "new-visit");
        var revisit = reader.ReadProcess(Environment.ProcessId, "test-owner", default);
        check("new navigation to same ID changes route key", revisit?.ThreadId == b && revisit.ViewKey != second?.ViewKey);
        server.Payload = Route("/", "new-visit");
        check("transport home does not retain previous ID", reader.ReadProcess(Environment.ProcessId, "test-owner", default)?.ThreadId is null);
        server.Payload = "null";
        var missing = reader.ReadProcess(Environment.ProcessId, "test-owner", default);
        check("missing router fails closed", missing?.ThreadId is null && missing?.LastError is not null);
        server.Payload = Route("/local/" + a, "new-visit");
        check("router recovery restores actual route", reader.ReadProcess(Environment.ProcessId, "test-owner", default)?.ThreadId == a);
        // A client that drops mid-request must not take the page down for later reads.
        using (var dropped = new TcpClient())
        {
            dropped.Connect(IPAddress.Loopback, server.Port);
            dropped.GetStream().WriteByte((byte)'G');
        }
        server.Payload = Route("/local/" + a, "after-disconnect");
        check("page survives a client that disconnects mid-request",
            reader.ReadProcess(Environment.ProcessId, "test-owner", default)?.ThreadId == a);
        server.Delay = true;
        var timer = Stopwatch.StartNew();
        var slow = reader.ReadProcess(Environment.ProcessId, "test-owner", default);
        check("hung renderer clears thread within bounded timeout", slow?.ThreadId is null && timer.ElapsedMilliseconds < 1500);
        using var wrongOwner = new CodexDebugRouteReader();
        check("other process cannot borrow listener", wrongOwner.ReadProcess(int.MaxValue, "wrong-owner", default) is null);
        check("native listener enumeration is process scoped", CodexDebugRouteReader.OwnedLoopbackPorts(Environment.ProcessId).Contains(server.Port)
            && !CodexDebugRouteReader.OwnedLoopbackPorts(int.MaxValue).Contains(server.Port));
        using var unsafeServer = new SyntheticCdpPage { ForeignEndpoint = true };
        using var unsafeReader = new CodexDebugRouteReader();
        // Retire the first endpoint so the remaining candidate is deliberately unsafe.
        server.Dispose();
        check("foreign websocket host is rejected", unsafeReader.ReadProcess(Environment.ProcessId, "unsafe", default) is null);
    }

}
