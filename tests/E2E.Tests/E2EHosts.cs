namespace Vuelto.E2E.Tests;

/// <summary>This repo's ports, handed to the packaged journey harness (Pkg 5.1d): the generated <c>LocalPorts</c>
/// (local-ports.props, Arch A10) is this project's, and the package cannot name it.</summary>
internal static class E2EHosts
{
#pragma warning disable CA2255 // a module initializer in a test assembly is the point: it runs before any journey resolves a host
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register()
    {
        PlatformE2EHosts.Web = LocalPorts.WebHttpsUrl;
        PlatformE2EHosts.Api = LocalPorts.ApiHttpsUrl;
        PlatformE2EHosts.MailUi = LocalPorts.MailUiUrl;
    }
}
