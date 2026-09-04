using System;

namespace DotNetCore.HayateOP;

public record HayatePoolOperationResult(string PoolName, string Message, DateTime Timestamp);