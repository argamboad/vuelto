using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Api.Endpoints;

namespace Vuelto.Api.Tests.Hosting;

/// <summary>
/// v4 T13 (ADV-P4-13, R155): two endpoints with the same method and pattern make every request to that route a
/// 500 (AmbiguousMatchException), before authorization, while the app boots normally. The CI test
/// <c>RouteGroupPrefixes_AreUnique</c> catches a shared group prefix; <see cref="RouteTableGuard"/> is the boot-time
/// backstop over the real route table. These tests build real <see cref="WebApplication"/>s with real maps.
/// </summary>
public class RouteTableGuardTests
{
    private static WebApplication NewApp()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddRouting();
        return builder.Build();
    }

    private static IEnumerable<Endpoint> EndpointsOf(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints);

    [Fact]
    public void TwoFeatureGroups_OnOnePrefix_FailWithBothEndpointsNamed()
    {
        var app = NewApp();
        app.MapTenantFeatureGroup("/api/reports").MapGet("/", () => "first").WithDisplayName("Reports.List");
        app.MapTenantFeatureGroup("/api/reports").MapGet("/", () => "second").WithDisplayName("Reports2.List");

        var ex = Assert.Throws<InvalidOperationException>(() => RouteTableGuard.EnsureUnique(EndpointsOf(app)));

        Assert.Contains("GET api/reports", ex.Message);
        Assert.Contains("Reports.List", ex.Message);
        Assert.Contains("Reports2.List", ex.Message);
    }

    [Fact]
    public void ParameterNames_DoNotMakeTwoRoutesDistinct()
    {
        var app = NewApp();
        app.MapGet("/api/notes/{id}", (string id) => id);
        app.MapGet("api/notes/{noteId}", (string noteId) => noteId);

        Assert.Throws<InvalidOperationException>(() => RouteTableGuard.EnsureUnique(EndpointsOf(app)));
    }

    [Fact]
    public void AnyMethodEndpoint_CollidesWithAMethodOne_OnTheSamePattern()
    {
        var app = NewApp();
        // ASP0022 (the analyzer) flags this pair only because both literals sit on one builder in one file; it
        // can't see across groups, slices or data sources, which is why the boot-time guard exists.
#pragma warning disable ASP0022
        app.Map("/api/ping", () => "any");
        app.MapPost("/api/ping", () => "post");
#pragma warning restore ASP0022

        Assert.Throws<InvalidOperationException>(() => RouteTableGuard.EnsureUnique(EndpointsOf(app)));
    }

    [Fact]
    public void DifferentMethods_ConstraintsOrOrder_AreNotCollisions()
    {
        var app = NewApp();
        app.MapGet("/api/notes/{id}", (string id) => id);
        app.MapPut("/api/notes/{id}", (string id) => id);
        app.MapGet("/api/items/{id:int}", (int id) => id);
        app.MapGet("/api/items/{slug}", (string slug) => slug);
        app.MapFallback("/api/{**rest}", () => Results.NotFound());
        app.MapGet("/api/{**rest}", () => "catch-all at the default order");

        RouteTableGuard.EnsureUnique(EndpointsOf(app));
    }

    [Fact]
    public void Program_RunsTheGuard_OverItsFinalRouteTable_RightBeforeRun()
    {
        // Any route mapped after the guard would escape it, so the call must be the last thing before app.Run().
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Api", "Program.cs")).Replace("\r\n", "\n");

        Assert.Matches(@"RouteTableGuard\.EnsureUnique\([^\n]*\);\s*\n\s*app\.Run\(\);\s*$", program);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Api", "Features")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root from the test assembly.");
    }
}
