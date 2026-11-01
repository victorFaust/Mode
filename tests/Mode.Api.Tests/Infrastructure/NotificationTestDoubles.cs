using System.Collections.Concurrent;
using Mode.Api.Notifications;
using Mode.Domain.Entities;

namespace Mode.Api.Tests.Infrastructure;

internal sealed class FakePushSender : IPushSender
{
    public ConcurrentQueue<(string UserId, string Endpoint, string Payload)> Sent { get; } = new();

    /// <summary>Endpoints that answer 410 Gone, like an unsubscribed browser.</summary>
    public ConcurrentDictionary<string, bool> Gone { get; } = new();

    public Task<PushResult> SendAsync(PushSubscription subscription, string payloadJson, CancellationToken cancellationToken)
    {
        if (Gone.ContainsKey(subscription.Endpoint)) return Task.FromResult(PushResult.Gone);
        Sent.Enqueue((subscription.UserId, subscription.Endpoint, payloadJson));
        return Task.FromResult(PushResult.Sent);
    }

    public IReadOnlyList<string> PayloadsFor(string userId) => Sent.Where(s => s.UserId == userId).Select(s => s.Payload).ToList();
}

internal sealed class FakeEmailSender : IEmailSender
{
    public ConcurrentQueue<(string To, string Subject, string Text, string Html)> Sent { get; } = new();

    public Task SendAsync(string toAddress, string toName, string subject, string textBody, string htmlBody, CancellationToken cancellationToken)
    {
        Sent.Enqueue((toAddress, subject, textBody, htmlBody));
        return Task.CompletedTask;
    }
}
