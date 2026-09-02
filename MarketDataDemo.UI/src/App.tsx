import { useEffect, useState, useCallback, useMemo } from 'react';
import { MantineProvider, LoadingOverlay, Text, Center, Stack } from '@mantine/core';
import '@mantine/core/styles.css';
import '@mantine/dates/styles.css';
import ChartToolbar from './components/ChartToolbar';
import CandleChart from './components/CandleChart';
import { fetchSymbols, fetchCandles, type Candle, type SymbolIntervals } from './api/candlesApi';

export default function App() {
  const [symbolData, setSymbolData] = useState<SymbolIntervals[]>([]);
  const [symbol, setSymbol] = useState('BTC/USDT');
  const [interval, setInterval_] = useState(1440);
  const [from, setFrom] = useState<string | null>('2024-01-01 00:00:00');
  const [to, setTo] = useState<string | null>('2024-06-01 00:00:00');
  const [candles, setCandles] = useState<Candle[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [initialized, setInitialized] = useState(false);

  const symbols = useMemo(() => symbolData.map((s) => s.symbol), [symbolData]);
  const intervals = useMemo(
    () => symbolData.find((s) => s.symbol === symbol)?.intervals ?? [],
    [symbolData, symbol],
  );

  useEffect(() => {
    fetchSymbols()
      .then((data) => {
        setSymbolData(data);
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
      <Stack gap={0} style={{ height: '100vh', overflow: 'hidden' }}>
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

        <div style={{ flex: 1, position: 'relative', overflow: 'hidden' }}>
          <LoadingOverlay visible={loading} />
          {error && (
            <Center h="100%" w="100%">
              <Text c="red" size="lg">{error}</Text>
            </Center>
          )}
          {!error && !loading && candles.length === 0 && (
            <Center h="100%" w="100%">
              <Text c="dimmed" size="lg">No data for the selected parameters.</Text>
            </Center>
          )}
          {candles.length > 0 && <CandleChart candles={candles} />}
        </div>
      </Stack>
    </MantineProvider>
  );
}
