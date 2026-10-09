using Planet.Protocol;

namespace Planet.Server;

// Owned by the room's channel loop/lock. Never persist conversations as account data.
public sealed class RoomChat
{
    public const int HistoryLimit = 50;
    private readonly Queue<ChatMessage> messages = new();
    public static bool Valid(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 100 &&
        !value.Any(c => char.IsControl(c) || c == '\u202e' || c == '\u202d' || c == '\u2066' || c == '\u2067' || c == '\u2068' || c == '\u2069');
    public void Add(ChatMessage message)
    {
        messages.Enqueue(message);
        while(messages.Count > HistoryLimit)messages.Dequeue();
    }
    public IEnumerable<ChatMessage> After(long cursor) => messages.Where(m => m.Id > cursor).Select(m => m.Clone());
}
