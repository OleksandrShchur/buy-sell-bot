namespace BuySellBot.Publishing;

public sealed class PublicationBlockedException(string message, string userMessage) : InvalidOperationException(message)
{
    public string UserMessage { get; } = userMessage;
}
