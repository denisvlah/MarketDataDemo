const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? '';

export interface CandleFileInfoDetail {
  symbol: string;
  intervalMinutes: number;
  path: string;
  start: string;
  end: string;
  version: number;
  fileSize: number;
  candleCount: number;
}

export interface Candle {
  t: string;
  o: number;
  h: number;
  l: number;
  c: number;
  v: number;
  n: number;
}

export async function fetchAllFiles(): Promise<CandleFileInfoDetail[]> {
  const res = await fetch(`${BASE_URL}/candles/files`);
  if (!res.ok) throw new Error(`Failed to fetch files: ${res.status}`);
  return res.json();
}

export async function fetchCandles(
  symbol: string,
  intervalMinutes: number,
  from: Date,
  to: Date,
): Promise<Candle[]> {
  const params = new URLSearchParams({
    from: from.toISOString(),
    to: to.toISOString(),
  });
  const res = await fetch(`${BASE_URL}/candles/${symbol}/${intervalMinutes}?${params}`);
  if (!res.ok) throw new Error(`Failed to fetch candles: ${res.status}`);
  return res.json();
}
