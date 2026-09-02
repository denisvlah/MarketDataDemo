import http from 'k6/http';
import { check, sleep } from 'k6';

// ── Configuration (override via --env or docker-compose environment) ───
const BASE_URL = __ENV.BASE_URL || 'http://localhost:5044';
const SYMBOL   = __ENV.SYMBOL   || 'XBT/USD';
const INTERVAL = __ENV.INTERVAL || '1';
const FROM     = __ENV.FROM     || '2024-01-01T00:00:00Z';
const TO       = __ENV.TO       || '2024-12-31T23:59:59Z';
const MAX_VUS  = parseInt(__ENV.MAX_VUS || '50', 10);

// ── Ramp-up stages ─────────────────────────────────────────────────────
//  Phase 1 (30s): warm up at 10% of MAX_VUS
//  Phase 2 (1m):  ramp up to 40% of MAX_VUS
//  Phase 3 (2m):  sustained load at MAX_VUS
//  Phase 4 (30s): graceful ramp-down
export const options = {
  stages: [
    { duration: '30s', target: Math.max(1, Math.round(MAX_VUS * 0.10)) },
    { duration: '1m',  target: Math.max(1, Math.round(MAX_VUS * 0.40)) },
    { duration: '2m',  target: MAX_VUS },
    { duration: '30s', target: 0 },
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
    `${BASE_URL}/candles/${INTERVAL}` +
    `?symbol=${encodeURIComponent(SYMBOL)}&from=${FROM}&to=${TO}`;

  const res = http.get(url, {
    headers: { Accept: 'application/json' },
    timeout: '30s',
  });

  check(res, {
    'status 200':    (r) => r.status === 200,
    'has body':      (r) => r.body && r.body.length > 2,  // more than "[]"
    'no stream error': (r) => !r.body || !r.body.trimEnd().endsWith('"error"]'),
  });

  sleep(1); // think time between requests per VU
}
