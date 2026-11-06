using Perezosoft.Testing.Suites;
using Xunit.Abstractions;

namespace Vuelto.Api.Tests;

/// <summary>Every tracked file classified, and the register files in step with the platform package this app references.</summary>
public class OwnershipTests(ITestOutputHelper output) : PlatformOwnershipSuite(output);
