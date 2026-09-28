using TaskMonitoring.Api.Security;

namespace Backend.Tests;

public sealed class PermissionCatalogTests
{
    [Fact]
    public void Permission_codes_are_unique_and_nonempty()
    {
        var permissions = PermissionCatalog.All.ToArray();

        Assert.NotEmpty(permissions);
        Assert.Equal(permissions.Length, permissions.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(permissions, string.IsNullOrWhiteSpace);
    }
}
