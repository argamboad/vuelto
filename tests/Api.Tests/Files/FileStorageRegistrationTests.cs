using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Infrastructure;
using Vuelto.Infrastructure.Files;

namespace Vuelto.Api.Tests.Files;

/// <summary>
/// FILES-3 (ADR-010): the storage backend is config-gated like the billing provider — S3 when a bucket
/// is configured, else local disk (so the app runs with zero cloud setup). Inspects the registered
/// <see cref="IFileStorage"/> descriptor; no provider is built, so nothing connects to a DB or S3.
/// </summary>
public class FileStorageRegistrationTests
{
    [Fact]
    public void S3Bucket_Configured_SelectsS3()
    {
        var impl = ResolveImpl(new()
        {
            ["Storage:S3:Bucket"] = "my-bucket",
            ["Storage:S3:Region"] = "us-east-1",
        });
        Assert.Equal(typeof(S3FileStorage), impl);
    }

    [Fact]
    public void NoS3Bucket_SelectsLocalDisk()
    {
        Assert.Equal(typeof(LocalDiskFileStorage), ResolveImpl(new()));
    }

    private static Type? ResolveImpl(Dictionary<string, string?> settings)
    {
        // A dummy connection string so AddInfrastructure's DbContext registration doesn't need a real DB.
        settings["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=x;Username=x;Password=x";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddInfrastructure(configuration, new FakeHostEnvironment(Environments.Development));

        return services.LastOrDefault(d => d.ServiceType == typeof(IFileStorage))?.ImplementationType;
    }
}
