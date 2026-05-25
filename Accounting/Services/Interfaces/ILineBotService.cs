namespace Accounting.Services.Interfaces;

public interface ILineBotService
{
    /// <summary>Issue a fresh 6-digit code that the user can send to the bot
    /// via "ผูก {code}" to bind their LINE userId. Expires in 10 minutes.</summary>
    Task<string> IssueBindCodeAsync(Guid userId);

    /// <summary>Handle an inbound LINE message event. Returns the text reply
    /// to send back to the user (or null to ignore).</summary>
    Task<string?> HandleMessageAsync(string lineUserId, string text);

    /// <summary>Verify the X-Line-Signature header matches the body
    /// signed by the channel secret. Used by the webhook controller.</summary>
    bool VerifySignature(string body, string? headerSignature);

    /// <summary>Reply to a user via the LINE Messaging API push endpoint
    /// using the configured ChannelAccessToken.</summary>
    Task ReplyAsync(string lineUserId, string text);
}
