import { useEffect, useState, useCallback, useMemo } from 'react';
import { MantineProvider, LoadingOverlay, Text, Center, Stack } from '@mantine/core';
import '@mantine/core/styles.css';
import '@mantine/dates/styles.css';
import ChartToolbar from './components/ChartToolbar';
import CandleChart from './components/CandleChart';
import { fetchAllFiles, fetchCandles, type Candle, type CandleFileInfoDetail } from './api/candlesApi';

export default function App() {
  const [files, setFiles] = useState<CandleFileInfoDetail[]>([]);
  const [symbol, setSymbol] = useState('BTCUSD');
  const [interval, setInterval_] = useState(1440);
  const [from, setFrom] = useState<string | null>('2025-01-01 00:00:00');
  const [to, setTo] = useState<string | null>('2026-01-01 00:00:00');
  const [candles, setCandles] = useState<Candle[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [initialized, setInitialized] = useState(false);

  const symbols = useMemo(() => [...new Set(files.map((f) => f.symbol))].sort(), [files]);
  const intervals = useMemo(
    () =>
      [...new Set(files.filter((f) => f.symbol === symbol).map((f) => f.intervalMinutes))].sort(
        (a, b) => a - b,
      ),
    [files, symbol],
  );

  useEffect(() => {
    fetchAllFiles()
      .then((f) => {
        setFiles(f);
        setInitialized(true);
      })
      .catch((e) => setError(e.message));
  }, []);

  useEffect(() => {
    if (initialized) loadCandles();
  }, [initialized]);

  const loadCandles = useCallback(async () => {
    if (!from || !to) return;
    setLoading(true);
    setError(null);
    try {
      const data = await fetchCandles(symbol, interval, new Date(from), new Date(to));
      setCandles(data);
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Unknown error');
      setCandles([]);
    } finally {
      setLoading(false);
    }
  }, [symbol, interval, from, to]);

  return (
    <MantineProvider defaultColorScheme="dark">
      <Stack gap={0} style={{ height: '100vh' }}>
        <ChartToolbar
          symbols={symbols}
          intervals={intervals}
          symbol={symbol}
          interval={interval}
          from={from}
          to={to}
          loading={loading}
          onSymbolChange={setSymbol}
          onIntervalChange={setInterval_}
          onFromChange={setFrom}
          onToChange={setTo}
          onLoad={loadCandles}
        />

        <div style={{ flex: 1, position: 'relative', padding: '0 16px 16px' }}>
          <LoadingOverlay visible={loading} />
          {error && (
            <Center h={500}>
              <Text c="red" size="lg">{error}</Text>
            </Center>
          )}
          {!error && !loading && candles.length === 0 && (
            <Center h={500}>
              <Text c="dimmed" size="lg">No data for the selected parameters.</Text>
            </Center>
          )}
          {candles.length > 0 && <CandleChart candles={candles} />}
        </div>
      </Stack>
    </MantineProvider>
  );
}
