using Pricing;

namespace Pricing.Tests;

public class DiscountTests
{
    [Fact]
    public void A_small_order_pays_the_full_price() => Assert.Equal(50m, Discount.Total(50m));
}
