# Hayate Object Pool

A high-performance, feature-rich object pool implementation for .NET, designed to optimize resource management and reduce garbage collection overhead.

## Features

- **High Performance Core**: Optimized for both single-threaded and multi-threaded scenarios.
- **Sharding Support**: Reduces lock contention through sharded architecture.
- **Auto-Scaling**: Dynamically adjusts pool size based on usage patterns.
- **Object Validation**: Validates objects on borrow, return, and while idle.
- **Eviction Policy**: Automatically removes expired or idle objects.
- **Generation Optimization**: Reduces validation frequency for long-lived objects.
- **Leak Detection**: Tracks and reports potential object leaks with stack traces.
- **Metrics & Monitoring**: Comprehensive statistics and snapshot capabilities.
- **Multiple Reject Policies**: Abort, Block, BlockTimeout, and CreateNew strategies.
- **Dependency Injection Integration**: Seamless integration with Microsoft.Extensions.DependencyInjection.
- **ASP.NET Core Endpoints**: Built-in management endpoints for monitoring and configuration.
- **Cross-Version Compatibility**: Supports .NET 6, .NET 8 and .NET 10.

## Installation

### NuGet Packages

| Package                                                | Description                                                                                          |
| :----------------------------------------------------- | :--------------------------------------------------------------------------------------------------- |
| `DotNetCore.HayateOP`                                | Core library with basic object pool functionality.                                                   |
| `DotNetCore.HayateOP.Extensions.DependencyInjection` | Seamless integration with Microsoft.Extensions.DependencyInjection.                                  |
| `DotNetCore.HayateOP.Extensions.Configuration`       | Integration with Microsoft.Extensions.Configuration for loading pool settings from appsettings.json. |
| `DotNetCore.HayateOP.Extensions.HealthCheck`         | ASP.NET Core Health Checks integration to monitor pool status in production.                         |
| `DotNetCore.HayateOP.Extensions.Endpoints`           | Built-in management endpoints for monitoring and configuration.                                      |
| `DotNetCore.HayateOP.Extensions.Diagnostics`         | Integration with System.Diagnostics for advanced performance monitoring and telemetry.               |

### .NET CLI

```bash
# Core package
dotnet add package DotNetCore.HayateOP

# Extensions
dotnet add package DotNetCore.HayateOP.Extensions.DependencyInjection
dotnet add package DotNetCore.HayateOP.Extensions.Configuration
dotnet add package DotNetCore.HayateOP.Extensions.HealthCheck
dotnet add package DotNetCore.HayateOP.Extensions.Endpoints
dotnet add package DotNetCore.HayateOP.Extensions.Diagnostics
```

## Quick Start

### Basic Usage

```csharp
using Hayate.ObjectPool;

// Create a simple pooled object
public class MyPooledObject : IHayateResettable
{
    public int Data { get; set; }

    public void Reset()
    {
        Data = 0;
    }
}

// Create and configure the pool
var pool = new HayatePoolBuilder<MyPooledObject>()
    .WithMinSize(10)
    .WithMaxSize(100)
    .Build();

// Acquire an object from the pool
var obj = pool.Acquire();
obj.Data = 42;

// Use the object...

// Return the object to the pool
pool.Release(obj);

// Dispose the pool when done
pool.Dispose();
```

### With Dependency Injection

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register Hayate Object Pool services
builder.Services.AddHayatePoolSupport()
       .RegisterHayatePool<MyPooledObject>(options =>
{
    options.MinPoolSize = 10;
    options.MaxPoolSize = 100;
    options.EnableValidation = true;
    options.EnableAutoScaling = true;
});

var app = builder.Build();

// Use the pool in your services
app.MapGet("/", (IHayateObjectPool<MyPooledObject> pool) =>
{
    var obj = pool.Acquire();
    try
    {
        // Use the object
        return Results.Ok("Object acquired successfully");
    }
    finally
    {
        pool.Release(obj);
    }
});

app.Run();
```

### With Management Endpoints

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHayatePoolSupport()
                 .RegisterHayatePool<MyPooledObject>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Map Hayate Object Pool management endpoints
app.MapHayatePoolEndpoints();

app.Run();
```

**Available Endpoints**:

* `GET /hayateop` **- Pool overview**
* `GET /hayateop/{poolName}` **- Pool details**
* `POST /hayateop/{poolName}` **- Update pool configuration**
* `GET /hayateop/{poolName}/stats` **- Pool statistics**
* `POST /hayateop/{poolName}/clear` **- Clear all objects from pool**

### Configuration Options

```csharp
var options = new HayatePoolOptions
{
    // Pool size configuration
    MinPoolSize = 10,
    MaxPoolSize = 100,

    // Sharding configuration
    EnableSharding = true,
    ShardCount = 4,
    UseFairMode = false,

    // Auto-scaling configuration
    EnableAutoScaling = true,
    ScalingIntervalMs = 30000,
    ScaleUpStep = 10,
    ScaleDownStep = 5,
    ScaleUpCooldownSeconds = 60,
    ScaleDownCooldownSeconds = 120,
    ScaleUpThreshold = 0.8,
    ScaleDownThreshold = 0.3,

    // Validation configuration
    EnableValidation = true,
    ValidateOnBorrow = true,
    ValidateOnReturn = true,
    ValidateWhileIdle = true,
    ValidateIntervalMs = 60000,

    // Eviction configuration
    EnableEviction = true,
    EvictionIntervalMs = 60000,
    MaxLifeTime = TimeSpan.FromHours(1),
    MaxIdleTime = TimeSpan.FromMinutes(30),
    SoftMinEvictableIdleTime = TimeSpan.FromMinutes(15),
    NumTestsPerEvictionRun = 10,

    // Generation optimization
    EnableGenerationOptimization = true,
    GenerationThresholdMs = 30000,
    OldGenerationValidationInterval = 5,

    // Leak detection
    EnableLeakDetection = true,
    LeakDetectionThreshold = TimeSpan.FromMinutes(5),

    // Metrics
    EnableMetrics = true,

    // Reject policy
    RejectPolicy = HayatePoolRejectPolicy.BlockTimeout,
    DefaultAcquireTimeout = TimeSpan.FromSeconds(5),

    // Object creation
    CreationRetryCount = 3,
    CreationRetryDelay = TimeSpan.FromMilliseconds(100)
};
```

### Advanced Usage

#### Custom Object Policy

```csharp
public class CustomPooledObjectPolicy : IHayatePooledObjectPolicy<MyCustomObject>
{
    public MyCustomObject Create()
    {
        return new MyCustomObject
        {
            CreatedAt = DateTime.UtcNow,
            InstanceId = Guid.NewGuid()
        };
    }

    public bool Validate(MyCustomObject obj)
    {
        return obj != null && !obj.IsDisposed;
    }

    public void OnAcquire(MyCustomObject obj)
    {
        obj.LastAcquiredAt = DateTime.UtcNow;
        obj.AcquireCount++;
    }

    public void OnPassivate(MyCustomObject obj)
    {
        obj.Reset();
    }

    public void OnRelease(MyCustomObject obj)
    {
        obj.LastReleasedAt = DateTime.UtcNow;
    }

    public void OnDestroy(MyCustomObject obj)
    {
        obj.Dispose();
    }
}

// Use the custom policy
var pool = new HayatePoolBuilder<MyCustomObject>()
    .WithPolicy(new CustomPooledObjectPolicy())
    .WithMinSize(10)
    .WithMaxSize(100)
    .Build();
```

### Monitoring and Diagnostics

#### Get current statistics

```csharp
var stats = pool.GetStats();

Console.WriteLine( $ "Pooled objects: {stats.PooledCount}");
Console.WriteLine( $ "Total created: {stats.TotalCreated}");
Console.WriteLine( $ "Total acquired: {stats.TotalAcquired}");
Console.WriteLine( $ "Total released: {stats.TotalReleased}");
Console.WriteLine( $ "Total missed: {stats.TotalMissed}");
Console.WriteLine( $ "Leak detected: {stats.LeakDetectedCount}");
```

#### Take a snapshot for diagnostics

```csharp
var snapshot = pool.TakeSnapshot();

Console.WriteLine( $ "Snapshot taken at: {snapshot.Timestamp}");
Console.WriteLine( $ "Borrowed count: {snapshot.BorrowedCount}");
Console.WriteLine( $ "Leak count: {snapshot.LeakCount}");

foreach (var leakTrace in snapshot.LeakTraces)
{
    Console.WriteLine( $ "Leak trace: {leakTrace}");
}
```

#### Reload configuration at runtime

```csharp
pool.ReloadConfig(config => {
    config.MaxPoolSize = 200;
    config.EnableAutoScaling = true;
})'
```

#### Clear all objects

```csharp
pool.Clear();
```

## Contributing

Contributions are welcome.

Please feel free to submit issues, feature requests, or pull requests.

### Development Requirements

- .NET 10 SDK (for development)
- .NET 6 SDK (for compatibility testing)
- Visual Studio 2026, VS Code, or Rider

### Building the Project

```bash
# Restore dependencies
dotnet restore

# Build the solution
dotnet build

# Run tests
dotnet test
```

## License

This project is licensed under the MIT License - see the [LICENSE] file for details.
