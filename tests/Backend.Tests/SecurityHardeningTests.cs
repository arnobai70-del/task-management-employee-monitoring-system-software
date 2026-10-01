using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Controllers;
using TaskMonitoring.Api.Security;

namespace Backend.Tests;

public sealed class SecurityHardeningTests
{
    [Fact]
    public void Authorization_registration_requires_authentication_by_default_and_registers_every_permission()
    {
        var services = new ServiceCollection();
        services.AddTaskMonitoringAuthorization();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        Assert.NotNull(options.FallbackPolicy);
        Assert.Contains(options.FallbackPolicy!.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);

        foreach (var permission in PermissionCatalog.All)
        {
            var policy = options.GetPolicy(permission);
            Assert.NotNull(policy);
            Assert.Contains(policy!.Requirements, requirement =>
                requirement is PermissionRequirement permissionRequirement && permissionRequirement.Permission == permission);
        }
    }

    [Fact]
    public void Forwarded_headers_require_an_explicit_trusted_proxy_or_network()
    {
        var missingTrustBoundary = new ReverseProxyOptions { TrustForwardedHeaders = true };

        var error = Assert.Throws<InvalidOperationException>(() =>
            SecurityRegistration.ConfigureTrustedForwardedHeaders(new ServiceCollection(), missingTrustBoundary));

        Assert.Contains("requires at least one", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forwarded_headers_use_only_configured_trust_boundaries()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        SecurityRegistration.ConfigureTrustedForwardedHeaders(
            services,
            new ReverseProxyOptions
            {
                TrustForwardedHeaders = true,
                KnownProxies = ["127.0.0.1"],
                KnownNetworks = ["10.77.0.0/24"]
            });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
        Assert.True(options.RequireHeaderSymmetry);
        Assert.Single(options.KnownProxies);
        Assert.Equal(IPAddress.Loopback, options.KnownProxies[0]);
        Assert.Single(options.KnownIPNetworks);
        Assert.Equal(System.Net.IPNetwork.Parse("10.77.0.0/24"), options.KnownIPNetworks[0]);
    }

    [Fact]
    public void Anonymous_controller_surface_is_explicit_and_limited()
    {
        var actual = typeof(AuthController).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type =>
            {
                var classAllowsAnonymous = type.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) is not null;
                return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any())
                    .Where(method => classAllowsAnonymous || method.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) is not null)
                    .Select(method => $"{type.Name}.{method.Name}");
            })
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        string[] expected =
        [
            "AgentUpdateDeviceController.GetPlan",
            "AgentUpdateDeviceController.RecordStatus",
            "AgentUpdateDeviceController.Register",
            "AuthController.Login",
            "AuthController.Refresh",
            "AuthController.WebLogin",
            "AuthController.WebRefresh"
        ];

        Assert.Equal(expected, actual);
    }
}
