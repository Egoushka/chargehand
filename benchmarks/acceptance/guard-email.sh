set -e
cat > test/GuardClauses.UnitTests/ChargehandBenchEmail.cs <<'EOF'
using System;
using Ardalis.GuardClauses;
using Xunit;

namespace GuardClauses.UnitTests;

public class ChargehandBenchEmail
{
    [Theory]
    [InlineData("a@corp.test")]
    [InlineData("first.last@corp.test")]
    [InlineData("a_b-c.9@corp.example")]
    [InlineData("Mixed.Case@CORP.TEST")]
    [InlineData("abcdefghij0123456789@corp.example")]
    public void ReturnsAValidAddress(string address)
    {
        Assert.Equal(address, Guard.Against.InvalidEmail(address, "email"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("corp.test")]
    [InlineData("@corp.test")]
    [InlineData("a@gmail.com")]
    [InlineData("a@corp.testing")]
    [InlineData("a@sub.corp.test")]
    [InlineData("a@@corp.test")]
    [InlineData("a b@corp.test")]
    [InlineData("a+tag@corp.test")]
    [InlineData("abcdefghij01234567890@corp.test")]
    public void ThrowsArgumentExceptionForAnInvalidAddress(string address)
    {
        var ex = Assert.Throws<ArgumentException>(() => Guard.Against.InvalidEmail(address, "email"));
        Assert.Equal("email", ex.ParamName);
    }

    [Fact]
    public void ThrowsArgumentNullExceptionForNull()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Guard.Against.InvalidEmail(null!, "email"));
        Assert.Equal("email", ex.ParamName);
    }

    [Fact]
    public void UsesTheCallerExpressionAsTheParameterName()
    {
        var contact = "nobody";
        var ex = Assert.Throws<ArgumentException>(() => Guard.Against.InvalidEmail(contact));
        Assert.Equal("contact", ex.ParamName);
    }

    [Fact]
    public void UsesTheCustomMessageAndException()
    {
        var ex = Assert.Throws<ArgumentException>(() => Guard.Against.InvalidEmail("nobody", "email", "custom text"));
        Assert.Contains("custom text", ex.Message);
        Assert.Throws<InvalidOperationException>(() => Guard.Against.InvalidEmail("nobody", "email", exceptionCreator: () => new InvalidOperationException()));
    }
}
EOF
DOTNET_ROLL_FORWARD=Major dotnet test test/GuardClauses.UnitTests --nologo -v q --filter "FullyQualifiedName~ChargehandBenchEmail"
