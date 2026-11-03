using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

// Load test: N connected people in group chats, each sending a message every few seconds.
// Users come from db/host-mock/loadtest-users.sql; tokens from the mock issuer. See docs/MONITORING.md.
var options = Options.Parse(args);
Console.WriteLine($"Mode load test: {options.Users} users, groups of {options.GroupSize}, one message per user every {options.IntervalSeconds}s for {options.Minutes} min");

using var http = new HttpClient { BaseAddress = new Uri(options.Api), Timeout = TimeSpan.FromSeconds(30) };
using var issuer = new HttpClient { BaseAddress = new Uri(options.Issuer) };
var users = Enumerable.Range(1, options.Users).Select(i => $"0a1b0002-0000-4000-8000-{i:D12}").ToArray();

var tokens = new string[users.Length];
await Parallel.ForEachAsync(Enumerable.Range(0, users.Length), new ParallelOptions { MaxDegreeOfParallelism = 20 }, async (i, ct) =>
{
    var response = await issuer.PostAsJsonAsync("/token", new { sub = users[i], name = $"Load User {i + 1}", lifetimeMinutes = 240 }, ct);
    response.EnsureSuccessStatusCode();
    tokens[i] = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("access_token").GetString()!;
});
Console.WriteLine($"Tokens issued: {tokens.Length}");

HttpRequestMessage Request(HttpMethod method, string url, int user, object? body = null)
{
    var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens[user]);
    return request;
}

// One group chat per GroupSize people; the first person creates it.
var groupOf = new Guid[users.Length];
for (var start = 0; start < users.Length; start += options.GroupSize)
{
    var members = Enumerable.Range(start, Math.Min(options.GroupSize, users.Length - start)).ToArray();
    if (members.Length < 3) { foreach (var m in members) groupOf[m] = groupOf[start - 1]; continue; }
    var response = await http.SendAsync(Request(HttpMethod.Post, "/api/v1/conversations", members[0],
        new { type = "Group", userIds = members.Skip(1).Select(m => users[m]).ToArray(), name = $"Load {start / options.GroupSize + 1}" }));
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Create group failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    foreach (var m in members) groupOf[m] = id;
}
Console.WriteLine($"Group chats: {groupOf.Distinct().Count()}");

var deliveryMs = new ConcurrentBag<double>();
var sendMs = new ConcurrentBag<double>();
var statuses = new ConcurrentDictionary<int, int>();
var connectFailures = 0;
var connections = new HubConnection?[users.Length];
var connectWatch = Stopwatch.StartNew();
await Parallel.ForEachAsync(Enumerable.Range(0, users.Length), new ParallelOptions { MaxDegreeOfParallelism = 50 }, async (i, ct) =>
{
    var connection = new HubConnectionBuilder()
        .WithUrl(new Uri(http.BaseAddress!, "/hubs/mode"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(tokens[i]))
        .Build();
    connection.On<JsonElement>("messageCreated", e =>
    {
        var body = e.GetProperty("message").GetProperty("bodyMarkdown").GetString();
        if (body is not null && body.StartsWith("lt:") && long.TryParse(body.AsSpan(3), out var sentTicks))
            deliveryMs.Add(TimeSpan.FromTicks(DateTime.UtcNow.Ticks - sentTicks).TotalMilliseconds);
    });
    try
    {
        await connection.StartAsync(ct);
        await connection.InvokeAsync<JsonElement>("JoinRooms", new[] { new { room = new { type = "Conversation", id = groupOf[i] }, lastSequence = 0 } }, ct);
        connections[i] = connection;
    }
    catch (Exception e)
    {
        Interlocked.Increment(ref connectFailures);
        Console.WriteLine($"Connect failed for user {i + 1}: {e.Message}");
    }
});
Console.WriteLine($"Connected: {users.Length - connectFailures}/{users.Length} in {connectWatch.Elapsed.TotalSeconds:F1}s");

var stopAt = DateTime.UtcNow.AddMinutes(options.Minutes);
var sent = 0;
await Task.WhenAll(Enumerable.Range(0, users.Length).Where(i => connections[i] is not null).Select(async i =>
{
    var random = new Random(i);
    await Task.Delay(random.Next(options.IntervalSeconds * 1000));
    while (DateTime.UtcNow < stopAt)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(Request(HttpMethod.Post, $"/api/v1/conversations/{groupOf[i]}/messages", i,
                new { id = Guid.NewGuid(), bodyMarkdown = $"lt:{DateTime.UtcNow.Ticks}" }));
            sendMs.Add(watch.Elapsed.TotalMilliseconds);
            statuses.AddOrUpdate((int)response.StatusCode, 1, (_, n) => n + 1);
            if (response.IsSuccessStatusCode) Interlocked.Increment(ref sent);
        }
        catch (Exception)
        {
            statuses.AddOrUpdate(0, 1, (_, n) => n + 1);
        }
        await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds) + TimeSpan.FromMilliseconds(random.Next(-500, 500)));
    }
}));

await Task.Delay(TimeSpan.FromSeconds(5));
var connected = connections.Count(c => c?.State == HubConnectionState.Connected);
// Everyone in the chat receives each message, including the sender's other connections.
var expected = (long)sent * options.GroupSize;

Console.WriteLine();
Console.WriteLine($"Still connected at end: {connected}/{users.Length}");
Console.WriteLine($"Messages sent (2xx): {sent}; status codes: {string.Join(", ", statuses.OrderBy(s => s.Key).Select(s => $"{(s.Key == 0 ? "error" : s.Key)}={s.Value}"))}");
Console.WriteLine($"Send latency ms   p50={Percentile(sendMs, 50):F0} p95={Percentile(sendMs, 95):F0} p99={Percentile(sendMs, 99):F0} max={sendMs.DefaultIfEmpty().Max():F0}");
Console.WriteLine($"Delivery latency  p50={Percentile(deliveryMs, 50):F0} p95={Percentile(deliveryMs, 95):F0} p99={Percentile(deliveryMs, 99):F0} max={deliveryMs.DefaultIfEmpty().Max():F0}");
Console.WriteLine($"Deliveries: {deliveryMs.Count} of about {expected} expected ({(expected == 0 ? 0 : 100.0 * deliveryMs.Count / expected):F1}%)");

foreach (var connection in connections.Where(c => c is not null))
    await connection!.DisposeAsync();
return connectFailures == 0 && statuses.Keys.All(k => k is >= 200 and < 300) ? 0 : 1;

static double Percentile(IEnumerable<double> values, int p)
{
    var sorted = values.Order().ToArray();
    return sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1)];
}

internal sealed record Options(string Api, string Issuer, int Users, int GroupSize, int IntervalSeconds, double Minutes)
{
    public static Options Parse(string[] args)
    {
        string Get(string name, string fallback)
        {
            var index = Array.IndexOf(args, "--" + name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        return new Options(
            Get("api", "http://localhost:5080"), Get("issuer", "http://localhost:5100"),
            int.Parse(Get("users", "200")), int.Parse(Get("group", "10")),
            int.Parse(Get("interval", "10")), double.Parse(Get("minutes", "2"), System.Globalization.CultureInfo.InvariantCulture));
    }
}
