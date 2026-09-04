using System;

namespace DotNetCore.HayateOP;

public record HayatePoolDetail(string PoolName, HayatePoolOptions Config, HayatePoolStats Stats);