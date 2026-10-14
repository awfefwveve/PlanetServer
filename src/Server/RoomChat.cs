using Planet.Protocol;

namespace Planet.Server;

// Owned by the room's channel loop/lock. Never persist conversations as account data.
public sealed class RoomChat
{
    public const int HistoryLimit = 50;
    private readonly Queue<ChatMessage> messages = new();
    // Highest message ID already included in a server push for this room.
    private long pushedThrough;
    public static bool Valid(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 100 &&
        !value.Any(c => char.IsControl(c) || c == '‮' || c == '‭' || c == '⁦' || c == '⁧' || c == '⁨' || c == '⁩');
    public void Add(ChatMessage message)
    {
        messages.Enqueue(message);
        while(messages.Count > HistoryLimit)messages.Dequeue();
    }
    public IEnumerable<ChatMessage> After(long cursor) => messages.Where(m => m.Id > cursor).Select(m => m.Clone());
    // Messages not yet broadcast by the tick. Each message is pushed once per room;
    // a reply may repeat it, so clients de-duplicate by ID.
    public ChatMessage[] TakeUnpushed()
    {
        var fresh = After(pushedThrough).ToArray();
        if(fresh.Length > 0)pushedThrough = fresh[^1].Id;
        return fresh;
    }
}
