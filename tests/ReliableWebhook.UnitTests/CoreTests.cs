using Xunit;
using System.Text.Json;
using ReliableWebhook.Application;
using ReliableWebhook.Domain;
namespace ReliableWebhook.UnitTests;

public sealed class CoreTests
{
    [Theory][InlineData("https://example.com/hook", true)][InlineData("ftp://example.com", false)][InlineData("relative", false)][InlineData("https://u:p@example.com", false)] public void ValidatesUrls(string url, bool expected) => Assert.Equal(expected, DestinationUrlValidator.IsValid(url));
    [Fact] public void HmacIsStableAndConstantTimeVerifiable() { var s = WebhookSigner.Sign("secret", "{\"a\":1}"); Assert.Equal("aa9e2e3575f5d7098b6caccd790888c36d5fdb63342a73bada2d6a51747a8494", s); Assert.True(WebhookSigner.Verify("secret", "{\"a\":1}", s)); Assert.False(WebhookSigner.Verify("secret", "changed", s)); }
    [Fact] public void RetryIsExponentialAndCapped() { Assert.Equal(TimeSpan.FromSeconds(5), RetryPolicy.Delay(1, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1))); Assert.Equal(TimeSpan.FromSeconds(40), RetryPolicy.Delay(4, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1))); Assert.Equal(TimeSpan.FromMinutes(1), RetryPolicy.Delay(99, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1))); }
    [Fact] public void DeliveryStatesAreExplicit() { Assert.Contains(DeliveryStatus.Processing, Enum.GetValues<DeliveryStatus>()); Assert.Contains(DeliveryStatus.FailedTerminal, Enum.GetValues<DeliveryStatus>()); }
    [Fact] public void EquivalentPayloadHasSameIdempotencyHash() { using var a = JsonDocument.Parse("{\"value\":1}"); Assert.Equal(PayloadHasher.Hash("created", a.RootElement), PayloadHasher.Hash("created", a.RootElement)); Assert.NotEqual(PayloadHasher.Hash("created", a.RootElement), PayloadHasher.Hash("updated", a.RootElement)); }
}
