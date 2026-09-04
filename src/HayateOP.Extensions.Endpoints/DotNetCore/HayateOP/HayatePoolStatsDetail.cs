using System;

namespace DotNetCore.HayateOP;

public record HayatePoolStatsDetail(string PoolName, HayatePoolStats Stats, HayatePoolSnapshot Snapshot);