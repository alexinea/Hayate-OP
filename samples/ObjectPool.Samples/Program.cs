// See https://aka.ms/new-console-template for more information

using DotNetCore.Extensions.ObjectPool;

Console.WriteLine("Hello, World!");

var pool = new ObjectPool<System.IO.MemoryStream>(
    objectGenerator: () => new(),
    maxSize: 20);

var ms = pool.Get();

try
{
    // do something with ms
}
finally
{
    pool.Return(ms);
}