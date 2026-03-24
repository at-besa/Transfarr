using System;
using System.Collections.Generic;
using Transfarr.Shared.Models;

namespace Transfarr.Node.Core;

public class SystemLogger
{
    public event Action<LogEntry>? OnLog;
    private readonly List<LogEntry> history = new();
    private readonly object historyLock = new();

    public IReadOnlyList<LogEntry> History 
    {
        get
        {
            lock (historyLock) { return history.ToArray(); }
        }
    }

    public void LogInfo(string message) => Emit(Transfarr.Shared.Models.LogLevel.Info, message);
    public void LogWarning(string message) => Emit(Transfarr.Shared.Models.LogLevel.Warning, message);
    public void LogError(string message) => Emit(Transfarr.Shared.Models.LogLevel.Error, message);

    private void Emit(Transfarr.Shared.Models.LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (historyLock)
        {
            history.Insert(0, entry);
            if (history.Count > 100) history.RemoveAt(history.Count - 1);
        }
        Console.WriteLine($"[{level.ToString().ToUpperInvariant()}] {message}");
        OnLog?.Invoke(entry);
    }
}
