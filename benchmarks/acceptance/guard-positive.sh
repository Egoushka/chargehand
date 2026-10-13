set -e
cat > test/GuardClauses.UnitTests/ChargehandBenchPositive.cs <<'EOF'
using System;
using Ardalis.GuardClauses;
using Xunit;

namespace GuardClauses.UnitTests;

public class ChargehandBenchPositive
{
    [Fact]
    public void ReturnsZeroAndNegativeValues()
    {
        Assert.Equal(0, Guard.Against.Positive(0, "x"));
        Assert.Equal(-3, Guard.Against.Positive(-3, "x"));
        Assert.Equal(-3L, Guard.Against.Positive(-3L, "x"));
        Assert.Equal(0m, Guard.Against.Positive(0m, "x"));
        Assert.Equal(-1.5m, Guard.Against.Positive(-1.5m, "x"));
        Assert.Equal(-1.5, Guard.Against.Positive(-1.5, "x"));
        Assert.Equal(-1.5f, Guard.Against.Positive(-1.5f, "x"));
    }

    [Fact]
    public void ThrowsArgumentExceptionForPositiveValues()
    {
        var ex = Assert.Throws<ArgumentException>(() => Guard.Against.Positive(1, "amount"));
        Assert.Equal("amount", ex.ParamName);
        Assert.Throws<ArgumentException>(() => Guard.Against.Positive(1L, "x"));
        Assert.Throws<ArgumentException>(() => Guard.Against.Positive(0.01m, "x"));
        Assert.Throws<ArgumentException>(() => Guard.Against.Positive(0.01, "x"));
        Assert.Throws<ArgumentException>(() => Guard.Against.Positive(0.01f, "x"));
    }

    [Fact]
    public void UsesTheCallerExpressionAsTheParameterName()
    {
        var quantity = 5;
        var ex = Assert.Throws<ArgumentException>(() => Guard.Against.Positive(quantity));
        Assert.Equal("quantity", ex.ParamName);
    }

    [Fact]
    public void UsesTheCustomMessage()
    {
        var ex = Assert.Throws<ArgumentException>(() => Guard.Against.Positive(1, "x", "custom text"));
        Assert.Contains("custom text", ex.Message);
    }

    [Fact]
    public void ThrowsTheCustomException()
    {
        Assert.Throws<InvalidOperationException>(() => Guard.Against.Positive(1, "x", exceptionCreator: () => new InvalidOperationException()));
    }
}
EOF
DOTNET_ROLL_FORWARD=Major dotnet test test/GuardClauses.UnitTests --nologo -v q --filter "FullyQualifiedName~ChargehandBenchPositive"
