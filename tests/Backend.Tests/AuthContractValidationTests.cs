using System.ComponentModel.DataAnnotations;
using TaskMonitoring.Api.Contracts;

namespace Backend.Tests;

public sealed class AuthContractValidationTests
{
    [Fact]
    public void Positional_auth_records_keep_validation_metadata_on_constructor_parameters()
    {
        AssertParameterValidation<LoginRequest>("Email", typeof(RequiredAttribute), typeof(EmailAddressAttribute), typeof(MaxLengthAttribute));
        AssertParameterValidation<LoginRequest>("Password", typeof(RequiredAttribute), typeof(MinLengthAttribute), typeof(MaxLengthAttribute));
        AssertParameterValidation<RefreshRequest>("RefreshToken", typeof(RequiredAttribute), typeof(MaxLengthAttribute));
        AssertParameterValidation<LogoutRequest>("RefreshToken", typeof(RequiredAttribute), typeof(MaxLengthAttribute));
    }

    private static void AssertParameterValidation<T>(string parameterName, params Type[] expectedAttributeTypes)
    {
        var constructor = typeof(T).GetConstructors().Single();
        var parameter = constructor.GetParameters().Single(item => item.Name == parameterName);
        var parameterAttributeTypes = parameter.GetCustomAttributes(inherit: true).Select(attribute => attribute.GetType()).ToHashSet();

        foreach (var expectedAttributeType in expectedAttributeTypes)
        {
            Assert.Contains(expectedAttributeType, parameterAttributeTypes);
        }

        var property = typeof(T).GetProperty(parameterName)!;
        Assert.DoesNotContain(property.GetCustomAttributes(inherit: true), attribute => attribute is ValidationAttribute);
    }
}
