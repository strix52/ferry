namespace Ferry;

public sealed record ThreadSeparator(string Text, bool IsUnreadDivider = false)
{
    public bool IsSeparator => true;
}
