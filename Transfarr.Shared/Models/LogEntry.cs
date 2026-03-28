using System;

namespace Transfarr.Shared.Models;

public record LogEntry(DateTime Timestamp, LogLevel Level, string Message);
