using System;

namespace Transfarr.Shared.Models;

public class ChatMessage
{
    public string SenderId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsPrivate { get; set; }
    public bool IsMe { get; set; }
    public bool IsMention { get; set; }
}
