using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using TaskMonitoring.Api.Configuration;

namespace TaskMonitoring.Api.Security;

public static class SecurityRegistration
{
    public static IServiceCollection AddTaskMonitoringAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            foreach (var permission in PermissionCatalog.All)
            {
                options.AddPolicy(permission, policy => policy.Requirements.Add(new PermissionRequirement(permission)));
            }
        });

        return services;
    }

    public static void ConfigureTrustedForwardedHeaders(
        IServiceCollection services,
        ReverseProxyOptions reverseProxyOptions)
    {
        if (!reverseProxyOptions.TrustForwardedHeaders)
        {
            return;
        }

        var proxies = reverseProxyOptions.KnownProxies
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => ParseProxy(value.Trim()))
            .Distinct()
            .ToArray();
        var networks = reverseProxyOptions.KnownNetworks
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => ParseNetwork(value.Trim()))
            .Distinct()
            .ToArray();

        if (proxies.Length == 0 && networks.Length == 0)
        {
            throw new InvalidOperationException(
                "ReverseProxy:TrustForwardedHeaders requires at least one ReverseProxy:KnownProxies or ReverseProxy:KnownNetworks entry.");
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.RequireHeaderSymmetry = true;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (var proxy in proxies)
            {
                options.KnownProxies.Add(proxy);
            }

            foreach (var network in networks)
            {
                options.KnownIPNetworks.Add(network);
            }
        });
    }

    private static IPAddress ParseProxy(string value)
    {
        if (!IPAddress.TryParse(value, out var address))
        {
            throw new InvalidOperationException($"ReverseProxy:KnownProxies contains an invalid IP address: {value}");
        }

        return address;
    }

    private static IPNetwork ParseNetwork(string value)
    {
        if (!IPNetwork.TryParse(value, out var network))
        {
            throw new InvalidOperationException($"ReverseProxy:KnownNetworks contains an invalid CIDR network: {value}");
        }

        return network;
    }
}
