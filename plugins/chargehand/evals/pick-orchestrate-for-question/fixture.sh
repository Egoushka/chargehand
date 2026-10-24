#!/usr/bin/env bash
set -euo pipefail
mkdir -p src/clients
cat > src/clients/billing.py <<'EOF'
import time
import requests


def get_invoice(invoice_id):
    for attempt in range(3):
        r = requests.get(f"https://billing.example/invoices/{invoice_id}")
        if r.status_code < 500:
            return r.json()
        time.sleep(2 ** attempt)
    raise RuntimeError("billing unavailable")
EOF
cat > src/clients/search.py <<'EOF'
import requests

from clients.retry import with_retry


@with_retry(max_attempts=5, delay=0.5)
def query(text):
    return requests.get("https://search.example/q", params={"q": text}).json()
EOF
cat > src/clients/retry.py <<'EOF'
import time


def with_retry(max_attempts, delay):
    def wrap(fn):
        def inner(*a, **kw):
            for i in range(max_attempts):
                try:
                    return fn(*a, **kw)
                except Exception:
                    if i == max_attempts - 1:
                        raise
                    time.sleep(delay)
        return inner
    return wrap
EOF
