import http from 'k6/http';
import { check, sleep } from 'k6';

// ── Configuration (override via --env or docker-compose environment) ───
const BASE_URL = __ENV.BASE_URL || 'http://localhost:5044';
const SYMBOL   = __ENV.SYMBOL   || 'XBT/USD';
const INTERVAL = __ENV.INTERVAL || '1';
const FROM     = __ENV.FROM     || '2024-01-01T00:00:00Z';
const TO       = __ENV.TO       || '2024-12-31T23:59:59Z';

// ── Ramp-up stages ─────────────────────────────────────────────────────
//  Phase 1 (30s): warm up with 5 VUs
//  Phase 2 (1m):  ramp up to 20 VUs
//  Phase 3 (2m):  sustained load at 50 VUs
//  Phase 4 (30s): graceful ramp-down
export const options = {
  stages: [
    { duration: '30s', target: 5  },
    { duration: '1m',  target: 20 },
    { duration: '2m',  target: 50 },
    { duration: '30s', target: 0  },
  ],
  thresholds: {
    // Fail the run if more than 5% of requests fail
    http_req_failed:   ['rate<0.05'],
    // Fail the run if 95th-percentile response time exceeds 2s
    http_req_duration: ['p(95)<2000'],
  },
};

// ── Virtual user scenario ──────────────────────────────────────────────
export default function () {
  const url =
    `${BASE_URL}/candles/${encodeURIComponent(SYMBOL)}/${INTERVAL}` +
    `?from=${FROM}&to=${TO}`;

  const res = http.get(url, {
    headers: { Accept: 'application/json' },
    timeout: '30s',
  });

  check(res, {
    'status 200': (r) => r.status === 200,
    'has body':   (r) => r.body && r.body.length > 2,  // more than "[]"
  });

  sleep(1); // think time between requests per VU
}
