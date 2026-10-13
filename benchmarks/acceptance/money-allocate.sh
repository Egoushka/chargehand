set -e
cat > src/test/java/org/joda/money/ChargehandBenchAllocateTest.java <<'EOF'
package org.joda.money;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import java.util.List;
import java.util.Random;
import org.junit.jupiter.api.Test;

class ChargehandBenchAllocateTest {

    private static List<Money> parse(String... texts) {
        return java.util.Arrays.stream(texts).map(Money::parse).toList();
    }

    @Test
    void splitsIntoEqualPartsAndGivesTheRemainderToTheFirst() {
        assertEquals(parse("USD 33.34", "USD 33.33", "USD 33.33"), Money.parse("USD 100.00").allocate(3));
        assertEquals(parse("USD 0.03", "USD 0.02"), Money.parse("USD 0.05").allocate(2));
        assertEquals(parse("JPY 34", "JPY 33", "JPY 33"), Money.parse("JPY 100").allocate(3));
        assertEquals(parse("USD 7.00"), Money.parse("USD 7.00").allocate(1));
    }

    @Test
    void negativeAmountsGetTheirRemainderFromTheFirstToo() {
        assertEquals(parse("USD -0.02", "USD -0.02", "USD -0.01"), Money.parse("USD -0.05").allocate(3));
    }

    @Test
    void thePartsAlwaysAddUpToTheWhole() {
        Random random = new Random(5);
        for (int i = 0; i < 200; i++) {
            Money whole = Money.ofMinor(CurrencyUnit.of("EUR"), random.nextInt(2_000_001) - 1_000_000);
            int parts = 1 + random.nextInt(9);
            List<Money> shares = whole.allocate(parts);
            assertEquals(parts, shares.size());
            assertEquals(whole, Money.total(shares));
        }
    }

    @Test
    void anInvalidNumberOfPartsIsRefused() {
        assertThrows(IllegalArgumentException.class, () -> Money.parse("USD 1.00").allocate(0));
        assertThrows(IllegalArgumentException.class, () -> Money.parse("USD 1.00").allocate(-2));
    }

    @Test
    void splitsByRatios() {
        assertEquals(parse("USD 70.00", "USD 30.00"), Money.parse("USD 100.00").allocate(70L, 30L));
        assertEquals(parse("USD 3.34", "USD 3.33", "USD 3.33"), Money.parse("USD 10.00").allocate(1L, 1L, 1L));
        assertEquals(parse("USD 0.02", "USD 0.03"), Money.parse("USD 0.05").allocate(1L, 2L));
        assertEquals(parse("JPY 3", "JPY 2"), Money.parse("JPY 5").allocate(1L, 1L));
    }

    @Test
    void aZeroRatioGetsNothing() {
        assertEquals(parse("USD 0.00", "USD 0.02", "USD 0.01"), Money.parse("USD 0.03").allocate(0L, 1L, 1L));
    }

    @Test
    void ratiosAddUpToTheWholeToo() {
        Random random = new Random(9);
        for (int i = 0; i < 200; i++) {
            Money whole = Money.ofMinor(CurrencyUnit.of("GBP"), random.nextInt(200_001) - 100_000);
            long[] ratios = new long[1 + random.nextInt(5)];
            ratios[random.nextInt(ratios.length)] = 1 + random.nextInt(50);
            for (int j = 0; j < ratios.length; j++) {
                if (ratios[j] == 0 && random.nextBoolean()) {
                    ratios[j] = random.nextInt(50);
                }
            }
            List<Money> shares = whole.allocate(ratios);
            assertEquals(ratios.length, shares.size());
            assertEquals(whole, Money.total(shares));
            for (int j = 0; j < ratios.length; j++) {
                if (ratios[j] == 0) {
                    assertEquals(Money.zero(CurrencyUnit.of("GBP")), shares.get(j));
                }
            }
        }
    }

    @Test
    void invalidRatiosAreRefused() {
        Money money = Money.parse("USD 1.00");
        assertThrows(IllegalArgumentException.class, () -> money.allocate(new long[0]));
        assertThrows(IllegalArgumentException.class, () -> money.allocate(0L, 0L));
        assertThrows(IllegalArgumentException.class, () -> money.allocate(-1L, 2L));
    }
}
EOF
mvn -B -q test -Dtest=ChargehandBenchAllocateTest
