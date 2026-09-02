using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Vuelto.Api.Tests.Infrastructure;

/// <summary>Minimal <see cref="IHostEnvironment"/> test double — only the environment name matters.</summary>
public sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
