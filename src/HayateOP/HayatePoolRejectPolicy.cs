namespace DotNetCore.HayateOP;

/// <summary>
/// Rejection-policy enum defining how a request is handled when it is rejected.
/// </summary>
public enum HayatePoolRejectPolicy
{
    /// <summary>
    /// Throw an exception immediately to reject the request.
    /// </summary>
    Abort,
    
    /// <summary>
    /// Block and wait until an object becomes available or a timeout elapses.
    /// </summary>
    Block,
    
    /// <summary>
    /// The default policy: block and wait until an object becomes available or the timeout elapses,
    /// and throw if the timeout is exceeded.
    /// </summary>
    BlockTimeout,
    
    /// <summary>
    /// Wait for a return first and only create a new object once the acquire timeout has elapsed (contrary to what the name suggests, it does not immediately bypass the pool to create a new one).
    /// The created object is registered into the pool and can be returned and reused; on a cold-start first call against an empty pool, it pays at most one full-timeout delay.
    /// </summary>
    CreateNew,

    /// <summary>
    /// Create on demand: a request that finds no idle object is served by creating one straight away
    /// while the pool still has room to grow, without waiting out the acquire timeout. An empty pool
    /// goes through the same synchronous first-object creation the block policies use, so the first
    /// borrow of a cold pool returns immediately; the timeout is only ever paid when the pool already
    /// holds <c>MaxPoolSize</c> objects and every one of them is lent out, where the request waits for a
    /// return and then creates one anyway.
    /// This matches the reference <c>DefaultObjectPool</c> behaviour of "create on a miss instead of
    /// blocking" while keeping creation bounded by the configured capacity.
    /// </summary>
    CreateOnDemand
}