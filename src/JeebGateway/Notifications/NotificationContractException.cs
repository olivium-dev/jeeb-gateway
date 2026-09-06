namespace JeebGateway.Notifications;

/// <summary>A successful upstream response cannot satisfy the inbox wire contract.</summary>
public sealed class NotificationContractException : Exception
{
    public NotificationContractException() : base("The notifications response has an invalid contract.") { }
}
