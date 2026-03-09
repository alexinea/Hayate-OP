using System;
using DotNetCore.HayateOP.Logging;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;

namespace DotNetCore.HayateOP;

public class HayatePoolBuilder<T> where T : class, new()
{
    private readonly HayatePoolOptions _options = new();
    private IHayateObjectPolicy<T> _policy;
    private IHayateScalingStrategy _scalingStrategy;
    private IHayateMetrics _metrics;
    private IHayateLogger _logger;
    private string _poolName;

    public HayatePoolBuilder()
    {
        _policy = new DefaultHayateObjectPolicy<T>();
        _scalingStrategy = new ThresholdScalingStrategy();
        _metrics = EmptyHayateMetrics.Instance;
        _logger = new DefaultHayateLogger();
        _poolName = typeof(T).Name;
    }

    #region 功能开关配置
    /// <summary>
    /// 启用/禁用分片功能
    /// </summary>
    public HayatePoolBuilder<T> WithEnableSharding(bool enable = true)
    {
        _options.EnableSharding = enable;
        return this;
    }

    /// <summary>
    /// 启用/禁用自动扩缩容
    /// </summary>
    public HayatePoolBuilder<T> WithEnableAutoScaling(bool enable = true)
    {
        _options.EnableAutoScaling = enable;
        return this;
    }

    /// <summary>
    /// 启用/禁用对象验证
    /// </summary>
    public HayatePoolBuilder<T> WithEnableValidation(bool enable = true)
    {
        _options.EnableValidation = enable;
        return this;
    }

    /// <summary>
    /// 启用/禁用空闲对象驱逐
    /// </summary>
    public HayatePoolBuilder<T> WithEnableEviction(bool enable = true)
    {
        _options.EnableEviction = enable;
        return this;
    }

    /// <summary>
    /// 启用/禁用分代优化
    /// </summary>
    public HayatePoolBuilder<T> WithEnableGenerationOptimization(bool enable = true)
    {
        _options.EnableGenerationOptimization = enable;
        return this;
    }

    /// <summary>
    /// 启用/禁用泄漏检测
    /// </summary>
    public HayatePoolBuilder<T> WithEnableLeakDetection(bool enable = true)
    {
        _options.EnableLeakDetection = enable;
        return this;
    }

    /// <summary>
    /// 启用/禁用指标统计
    /// </summary>
    public HayatePoolBuilder<T> WithEnableMetrics(bool enable = true)
    {
        _options.EnableMetrics = enable;
        return this;
    }
    #endregion
    
    #region 基础配置
    /// <summary>
    /// 设置池名称
    /// </summary>
    public HayatePoolBuilder<T> WithPoolName(string name)
    {
        _poolName = name ?? throw new ArgumentNullException(nameof(name));
        return this;
    }

    /// <summary>
    /// 设置最小池大小
    /// </summary>
    public HayatePoolBuilder<T> WithMinSize(int minSize)
    {
        if (minSize < 0) throw new ArgumentOutOfRangeException(nameof(minSize), "MinSize cannot be negative");
        _options.MinPoolSize = minSize;
        return this;
    }

    /// <summary>
    /// 设置最大池大小
    /// </summary>
    public HayatePoolBuilder<T> WithMaxSize(int maxSize)
    {
        if (maxSize < 0) throw new ArgumentOutOfRangeException(nameof(maxSize), "MaxSize cannot be negative");
        _options.MaxPoolSize = maxSize;
        return this;
    }

    /// <summary>
    /// 设置分片数量
    /// </summary>
    public HayatePoolBuilder<T> WithShardCount(int shardCount)
    {
        if (shardCount < 1 || shardCount > 32)
            throw new ArgumentOutOfRangeException(nameof(shardCount), "ShardCount must be between 1 and 32");
        _options.ShardCount = shardCount;
        return this;
    }

    /// <summary>
    /// 设置是否启用公平模式
    /// </summary>
    public HayatePoolBuilder<T> WithFairMode(bool enable = true)
    {
        _options.UseFairMode = enable;
        return this;
    }
    #endregion

    #region 超时配置
    /// <summary>
    /// 设置默认获取超时时间
    /// </summary>
    public HayatePoolBuilder<T> WithAcquireTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be greater than zero");
        _options.DefaultAcquireTimeout = timeout;
        return this;
    }
    #endregion

    #region 扩缩容配置
    /// <summary>
    /// 设置扩缩容检查间隔
    /// </summary>
    public HayatePoolBuilder<T> WithScalingInterval(int intervalMs)
    {
        if (intervalMs < 100)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "ScalingInterval must be at least 100ms");
        _options.ScalingIntervalMs = intervalMs;
        return this;
    }

    /// <summary>
    /// 设置扩容阈值（使用率）
    /// </summary>
    public HayatePoolBuilder<T> WithScaleUpThreshold(double threshold)
    {
        if (threshold < 0 || threshold > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), "ScaleUpThreshold must be between 0 and 1");
        _options.ScaleUpThreshold = threshold;
        return this;
    }

    /// <summary>
    /// 设置缩容阈值（使用率）
    /// </summary>
    public HayatePoolBuilder<T> WithScaleDownThreshold(double threshold)
    {
        if (threshold < 0 || threshold > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), "ScaleDownThreshold must be between 0 and 1");
        _options.ScaleDownThreshold = threshold;
        return this;
    }

    /// <summary>
    /// 设置扩容冷却时间（秒）
    /// </summary>
    public HayatePoolBuilder<T> WithScaleUpCooldownSeconds(int seconds)
    {
        if (seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), "ScaleUpCooldownSeconds cannot be negative");
        _options.ScaleUpCooldownSeconds = seconds;
        return this;
    }

    /// <summary>
    /// 设置缩容冷却时间（秒）
    /// </summary>
    public HayatePoolBuilder<T> WithScaleDownCooldownSeconds(int seconds)
    {
        if (seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), "ScaleDownCooldownSeconds cannot be negative");
        _options.ScaleDownCooldownSeconds = seconds;
        return this;
    }

    /// <summary>
    /// 设置扩容步长
    /// </summary>
    public HayatePoolBuilder<T> WithScaleUpStep(int step)
    {
        if (step < 1)
            throw new ArgumentOutOfRangeException(nameof(step), "ScaleUpStep must be at least 1");
        _options.ScaleUpStep = step;
        return this;
    }
    #endregion

    #region 验证配置
    /// <summary>
    /// 设置是否在借出时验证对象
    /// </summary>
    public HayatePoolBuilder<T> WithValidateOnBorrow(bool enable = true)
    {
        _options.ValidateOnBorrow = enable;
        return this;
    }

    /// <summary>
    /// 设置是否在归还时验证对象
    /// </summary>
    public HayatePoolBuilder<T> WithValidateOnReturn(bool enable = true)
    {
        _options.ValidateOnReturn = enable;
        return this;
    }

    /// <summary>
    /// 设置是否在空闲时验证对象
    /// </summary>
    public HayatePoolBuilder<T> WithValidateWhileIdle(bool enable = true)
    {
        _options.ValidateWhileIdle = enable;
        return this;
    }

    /// <summary>
    /// 设置空闲验证间隔（毫秒）
    /// </summary>
    public HayatePoolBuilder<T> WithValidateInterval(int intervalMs)
    {
        if (intervalMs < 1000)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "ValidateInterval must be at least 1000ms");
        _options.ValidateIntervalMs = intervalMs;
        return this;
    }

    /// <summary>
    /// 设置老年代验证间隔（次数）
    /// </summary>
    public HayatePoolBuilder<T> WithOldGenerationValidationInterval(int interval)
    {
        if (interval < 1)
            throw new ArgumentOutOfRangeException(nameof(interval), "OldGenerationValidationInterval must be at least 1");
        _options.OldGenerationValidationInterval = interval;
        return this;
    }

    /// <summary>
    /// 设置分代升级阈值（毫秒）
    /// </summary>
    public HayatePoolBuilder<T> WithGenerationThreshold(int thresholdMs)
    {
        if (thresholdMs < 1000)
            throw new ArgumentOutOfRangeException(nameof(thresholdMs), "GenerationThreshold must be at least 1000ms");
        _options.GenerationThresholdMs = thresholdMs;
        return this;
    }
    #endregion

    #region 驱逐配置
    /// <summary>
    /// 设置对象最大生命周期
    /// </summary>
    public HayatePoolBuilder<T> WithMaxLifeTime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "MaxLifeTime must be greater than zero");
        _options.MaxLifeTime = lifetime;
        return this;
    }

    /// <summary>
    /// 设置对象最大空闲时间
    /// </summary>
    public HayatePoolBuilder<T> WithMaxIdleTime(TimeSpan idleTime)
    {
        if (idleTime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTime), "MaxIdleTime must be greater than zero");
        _options.MaxIdleTime = idleTime;
        return this;
    }

    /// <summary>
    /// 设置软最小空闲驱逐时间
    /// </summary>
    public HayatePoolBuilder<T> WithSoftMinEvictableIdleTime(TimeSpan idleTime)
    {
        if (idleTime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTime), "SoftMinEvictableIdleTime must be greater than zero");
        _options.SoftMinEvictableIdleTime = idleTime;
        return this;
    }

    /// <summary>
    /// 设置驱逐检查间隔（毫秒）
    /// </summary>
    public HayatePoolBuilder<T> WithEvictionInterval(int intervalMs)
    {
        if (intervalMs < 1000)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "EvictionInterval must be at least 1000ms");
        _options.EvictionIntervalMs = intervalMs;
        return this;
    }

    /// <summary>
    /// 设置每次驱逐检查的对象数量
    /// </summary>
    public HayatePoolBuilder<T> WithNumTestsPerEvictionRun(int count)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "NumTestsPerEvictionRun must be at least 1");
        _options.NumTestsPerEvictionRun = count;
        return this;
    }
    #endregion

    #region 创建配置
    /// <summary>
    /// 设置对象创建重试次数
    /// </summary>
    public HayatePoolBuilder<T> WithCreationRetryCount(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "CreationRetryCount cannot be negative");
        _options.CreationRetryCount = count;
        return this;
    }

    /// <summary>
    /// 设置对象创建重试延迟
    /// </summary>
    public HayatePoolBuilder<T> WithCreationRetryDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay), "CreationRetryDelay cannot be negative");
        _options.CreationRetryDelay = delay;
        return this;
    }
    #endregion

    #region 泄漏检测配置
    /// <summary>
    /// 设置泄漏检测阈值
    /// </summary>
    public HayatePoolBuilder<T> WithLeakDetectionThreshold(TimeSpan threshold)
    {
        if (threshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(threshold), "LeakDetectionThreshold must be greater than zero");
        _options.LeakDetectionThreshold = threshold;
        return this;
    }

    /// <summary>
    /// 设置是否启用泄漏检测
    /// </summary>
    public HayatePoolBuilder<T> WithLeakDetection(bool enable = true)
    {
        _options.EnableLeakDetection = enable;
        return this;
    }
    #endregion

    #region 拒绝策略配置
    /// <summary>
    /// 设置拒绝策略
    /// </summary>
    public HayatePoolBuilder<T> WithRejectPolicy(HayatePoolRejectPolicy policy)
    {
        _options.RejectPolicy = policy;
        return this;
    }
    #endregion

    #region 依赖注入配置
    /// <summary>
    /// 设置对象池策略
    /// </summary>
    public HayatePoolBuilder<T> WithPolicy(IHayateObjectPolicy<T> policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    /// <summary>
    /// 设置扩缩容策略
    /// </summary>
    public HayatePoolBuilder<T> WithScalingStrategy(IHayateScalingStrategy strategy)
    {
        _scalingStrategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        return this;
    }

    /// <summary>
    /// 设置指标收集器
    /// </summary>
    public HayatePoolBuilder<T> WithMetrics(IHayateMetrics metrics)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        return this;
    }

    /// <summary>
    /// 设置日志记录器
    /// </summary>
    public HayatePoolBuilder<T> WithLogger(IHayateLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        return this;
    }
    #endregion

    #region 全量配置
    /// <summary>
    /// 全量配置（覆盖所有选项）
    /// </summary>
    public HayatePoolBuilder<T> Configure(Action<HayatePoolOptions> configure)
    {
        configure(_options);
        return this;
    }
    #endregion

    public IHayateObjectPool<T> Build()
    {
        if (!_options.IsValid())
        {
            throw new InvalidOperationException("HayatePool configuration is invalid");
        }

        if (!_options.EnableMetrics)
        {
            _metrics = EmptyHayateMetrics.Instance;
        }

        var pool = new HayatePoolBasic<T>(
            _policy,
            _options,
            _scalingStrategy,
            _metrics,
            _logger,
            _poolName);

        pool.PreWarm();

        pool.StartBackgroundTasks();

        _logger.LogInformation("HayatePool [{PoolName}] initialized successfully", _poolName);

        return pool;
    }
}