using Vuelto.Host.Persistence;
using Vuelto.Api.Tests.Infrastructure;
using Perezosoft.Testing.Suites;

namespace Vuelto.Api.Tests;

/// <summary>This app's real host starts, reports ready, names the platform and serves a tenant call.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class CompositionSmokeTests(IntegrationTestFactory factory) : PlatformCompositionSmoke<Program, AppDbContext>(factory);
