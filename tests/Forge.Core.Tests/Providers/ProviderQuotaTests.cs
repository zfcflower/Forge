using System.Globalization;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class ProviderQuotaTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly ModelSettings Model = new("m-1", ProviderId: "prov");

    private static ProviderException Error(int status, string body, TimeSpan? retryAfter = null)
    {
        var exception = new ProviderException($"prov returned {status}: {body}", status is 408 or 429 or >= 500, status);
        if (retryAfter is { } delay)
        {
            exception.Data[ProviderErrors.RetryAfterKey] = delay;
        }

        return exception;
    }

    private static ProviderQuotaExhausted? Classify(ProviderException e) => ProviderQuota.Classify(e, Model, Now);

    [Fact]
    public void OpenAi_insufficient_quota_is_quota_with_unknown_reset()
    {
        var quota = Classify(Error(429, """{"error":{"code":"insufficient_quota","message":"You exceeded your current quota, please check your plan and billing details."}}"""));

        Assert.NotNull(quota);
        Assert.Equal("prov", quota.ProviderId);
        Assert.Equal("m-1", quota.Model);
        Assert.Null(quota.ResetAt);
        Assert.Contains("insufficient_quota", quota.Message);
    }

    [Fact]
    public void Anthropic_credit_balance_400_is_quota()
    {
        Assert.NotNull(Classify(Error(400, """{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Anthropic API."}}""")));
    }

    [Fact]
    public void Claude_subscription_long_retry_after_is_quota_resetting_after_retry_after()
    {
        var quota = Classify(Error(429, "rate limited", TimeSpan.FromHours(3)));

        Assert.NotNull(quota);
        Assert.Equal(Now + TimeSpan.FromHours(3), quota.ResetAt);
    }

    [Fact]
    public void Glm_1308_with_chinese_reset_time_parses_local_time()
    {
        var local = DateTime.Now.AddDays(2);
        var text = local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var now = new DateTimeOffset(DateTime.Now);
        var body = "{\"error\":{\"code\":\"1308\",\"message\":\"已达到 5 小时的使用上限。您的限额将在 " + text + " 重置。\"}}";

        var quota = ProviderQuota.Classify(Error(429, body), Model, now);

        Assert.NotNull(quota);
        Assert.Equal(new DateTimeOffset(DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)), quota.ResetAt);
    }

    [Theory]
    [InlineData("""{"error":{"code":"1113","message":"x"}}""")]
    [InlineData("""{"error":{"code":"1310","message":"x"}}""")]
    [InlineData("账户余额不足,请充值")]
    public void Zhipu_codes_and_chinese_wording_are_quota(string body) =>
        Assert.NotNull(Classify(Error(429, body)));

    [Fact]
    public void Kimi_exceeded_current_quota_is_quota() =>
        Assert.NotNull(Classify(Error(429, """{"error":{"type":"exceeded_current_quota_error","message":"x"}}""")));

    [Fact]
    public void Copilot_402_is_quota() => Assert.NotNull(Classify(Error(402, "You have no quota")));

    [Fact]
    public void Plain_429_is_not_quota()
    {
        Assert.Null(Classify(Error(429, "slow down")));
        Assert.Null(Classify(Error(429, "slow down", TimeSpan.FromSeconds(20))));
        Assert.Null(Classify(Error(429, "slow down", TimeSpan.FromMinutes(5))));
    }

    [Fact]
    public void Server_error_is_not_quota_even_with_marker_text() =>
        Assert.Null(Classify(Error(500, "usage limit billing quota exceeded")));

    [Fact]
    public void Unknown_400_is_not_quota() => Assert.Null(Classify(Error(400, "messages: field required")));

    [Fact]
    public void Error_without_status_is_not_quota() =>
        Assert.Null(Classify(new ProviderException("quota exceeded", retryable: false)));

    [Fact]
    public void Message_is_trimmed()
    {
        var quota = Classify(Error(402, new string('x', 2000)));

        Assert.NotNull(quota);
        Assert.Equal(500, quota.Message.Length);
    }

    [Fact]
    public void Iso_reset_timestamp_is_parsed()
    {
        var quota = Classify(Error(429, "usage limit reached. Resets at 2026-05-02T08:30:00Z"));

        Assert.Equal(new DateTimeOffset(2026, 5, 2, 8, 30, 0, TimeSpan.Zero), quota!.ResetAt);
    }

    [Fact]
    public void Chinese_reset_word_with_iso_offset_is_parsed()
    {
        var quota = Classify(Error(429, "使用上限,重置时间 2026-05-02T08:30:00+08:00"));

        Assert.Equal(new DateTimeOffset(2026, 5, 2, 0, 30, 0, TimeSpan.Zero), quota!.ResetAt!.Value.ToUniversalTime());
    }

    [Fact]
    public void Unix_seconds_after_reset_is_parsed()
    {
        var reset = Now.AddHours(5);
        var quota = Classify(Error(429, $"usage_limit_reached, resets_at: {reset.ToUnixTimeSeconds()}"));

        Assert.Equal(reset, quota!.ResetAt);
    }

    [Theory]
    [InlineData("quota exceeded, try again in 2h 30m", 150)]
    [InlineData("quota exceeded, try again in 45 minutes", 45)]
    [InlineData("usage limit, please retry in 1 hour and 10 minutes", 70)]
    public void Relative_reset_is_parsed(string body, int minutes)
    {
        var quota = Classify(Error(429, body));

        Assert.Equal(Now + TimeSpan.FromMinutes(minutes), quota!.ResetAt);
    }

    [Fact]
    public void Retry_after_wins_over_message_text()
    {
        var quota = Classify(Error(429, "usage limit, try again in 1 hour", TimeSpan.FromHours(2)));

        Assert.Equal(Now.AddHours(2), quota!.ResetAt);
    }

    [Fact]
    public void Reset_in_the_past_is_unknown()
    {
        var quota = Classify(Error(429, "usage limit reached. Resets at 2026-04-01T00:00:00Z"));

        Assert.NotNull(quota);
        Assert.Null(quota.ResetAt);
    }
}
