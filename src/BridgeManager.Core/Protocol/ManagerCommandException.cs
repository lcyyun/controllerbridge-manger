namespace BridgeManager.Core.Protocol;

public sealed class ManagerCommandException : Exception
{
    public ManagerCommandException(string message)
        : base(message)
    {
    }
}
