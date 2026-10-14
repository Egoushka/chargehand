set -e
cat > src/test/java/org/joda/money/ChargehandBenchMinMaxTest.java <<'EOF'
package org.joda.money;

import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertThrows;

import org.junit.jupiter.api.Test;

class ChargehandBenchMinMaxTest {

    private static final Money LOW = Money.parse("GBP 1.00");
    private static final Money HIGH = Money.parse("GBP 2.50");

    @Test
    void maxReturnsTheGreaterInEitherOrder() {
        assertSame(HIGH, Money.max(LOW, HIGH));
        assertSame(HIGH, Money.max(HIGH, LOW));
    }

    @Test
    void minReturnsTheLesserInEitherOrder() {
        assertSame(LOW, Money.min(LOW, HIGH));
        assertSame(LOW, Money.min(HIGH, LOW));
    }

    @Test
    void equalAmountsReturnTheFirstArgument() {
        Money first = Money.parse("GBP 1.00");
        Money second = Money.parse("GBP 1.00");
        assertSame(first, Money.max(first, second));
        assertSame(first, Money.min(first, second));
    }

    @Test
    void worksWithNegativeAmounts() {
        Money minusTwo = Money.parse("GBP -2.00");
        Money minusOne = Money.parse("GBP -1.00");
        assertSame(minusOne, Money.max(minusTwo, minusOne));
        assertSame(minusTwo, Money.min(minusTwo, minusOne));
    }

    @Test
    void differentCurrenciesAreRefused() {
        Money euro = Money.parse("EUR 1.00");
        assertThrows(CurrencyMismatchException.class, () -> Money.max(LOW, euro));
        assertThrows(CurrencyMismatchException.class, () -> Money.min(LOW, euro));
    }

    @Test
    void nullIsRefused() {
        assertThrows(NullPointerException.class, () -> Money.max(null, LOW));
        assertThrows(NullPointerException.class, () -> Money.max(LOW, null));
        assertThrows(NullPointerException.class, () -> Money.min(null, LOW));
        assertThrows(NullPointerException.class, () -> Money.min(LOW, null));
    }
}
EOF
mvn -B -q test -Dtest=ChargehandBenchMinMaxTest
