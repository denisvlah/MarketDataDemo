import { useCallback, useEffect, useRef, useState } from 'react';
import {
  createChart,
  type IChartApi,
  type ISeriesApi,
  CrosshairMode,
  ColorType,
  CandlestickSeries,
  HistogramSeries,
} from 'lightweight-charts';
import type { Candle } from '../api/candlesApi';

interface Props {
  candles: Candle[];
}

interface OhlcvData {
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
  change: number;
  changePercent: number;
  isUp: boolean;
}

const formatPrice = (p: number) =>
  p < 1 ? p.toPrecision(6) : p.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

const formatVolume = (v: number) => {
  if (v >= 1_000_000_000) return (v / 1_000_000_000).toFixed(2) + 'B';
  if (v >= 1_000_000) return (v / 1_000_000).toFixed(2) + 'M';
  if (v >= 1_000) return (v / 1_000).toFixed(2) + 'K';
  return v.toFixed(2);
};

export default function CandleChart({ candles }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<IChartApi | null>(null);
  const candleSeriesRef = useRef<ISeriesApi<'Candlestick'> | null>(null);
  const volumeSeriesRef = useRef<ISeriesApi<'Histogram'> | null>(null);
  const [ohlcv, setOhlcv] = useState<OhlcvData | null>(null);

  const handleCrosshairMove = useCallback((param: { time?: unknown; seriesData?: Map<unknown, unknown> }) => {
    if (!param.time || !param.seriesData) {
      setOhlcv(null);
      return;
    }

    const candleData = param.seriesData.get(candleSeriesRef.current) as
      | { open: number; high: number; low: number; close: number }
      | undefined;
    const volData = param.seriesData.get(volumeSeriesRef.current) as { value: number } | undefined;

    if (!candleData) {
      setOhlcv(null);
      return;
    }

    const change = candleData.close - candleData.open;
    const changePercent = candleData.open !== 0 ? (change / candleData.open) * 100 : 0;

    setOhlcv({
      open: candleData.open,
      high: candleData.high,
      low: candleData.low,
      close: candleData.close,
      volume: volData?.value ?? 0,
      change,
      changePercent,
      isUp: candleData.close >= candleData.open,
    });
  }, []);

  // Create chart once
  useEffect(() => {
    if (!containerRef.current) return;

    const chart = createChart(containerRef.current, {
      layout: {
        background: { type: ColorType.Solid, color: '#1a1b1e' },
        textColor: '#c1c2c5',
      },
      crosshair: { mode: CrosshairMode.Normal },
      grid: {
        vertLines: { color: '#2c2e33' },
        horzLines: { color: '#2c2e33' },
      },
      timeScale: {
        timeVisible: true,
        secondsVisible: false,
      },
      width: containerRef.current.clientWidth,
      height: containerRef.current.clientHeight,
    });

    const candleSeries = chart.addSeries(CandlestickSeries, {
      upColor: '#26a69a',
      downColor: '#ef5350',
      borderVisible: false,
      wickUpColor: '#26a69a',
      wickDownColor: '#ef5350',
    });

    const volumeSeries = chart.addSeries(HistogramSeries, {
      priceFormat: { type: 'volume' },
      priceScaleId: 'volume',
    });

    chart.priceScale('volume').applyOptions({
      scaleMargins: { top: 0.8, bottom: 0 },
    });

    chartRef.current = chart;
    candleSeriesRef.current = candleSeries;
    volumeSeriesRef.current = volumeSeries;

    chart.subscribeCrosshairMove(handleCrosshairMove);

    const handleResize = () => {
      if (containerRef.current) {
        chart.applyOptions({ 
          width: containerRef.current.clientWidth,
          height: containerRef.current.clientHeight,
        });
      }
    };
    window.addEventListener('resize', handleResize);

    return () => {
      window.removeEventListener('resize', handleResize);
      chart.remove();
      chartRef.current = null;
    };
  }, [handleCrosshairMove]);

  // Update data
  useEffect(() => {
    if (!candleSeriesRef.current || !volumeSeriesRef.current) return;

    const candleData = candles.map((c) => ({
      time: Math.floor(new Date(c.t).getTime() / 1000) as unknown as import('lightweight-charts').UTCTimestamp,
      open: c.o,
      high: c.h,
      low: c.l,
      close: c.c,
    }));

    const volumeData = candles.map((c) => ({
      time: Math.floor(new Date(c.t).getTime() / 1000) as unknown as import('lightweight-charts').UTCTimestamp,
      value: c.v,
      color: c.c >= c.o ? 'rgba(38,166,154,0.5)' : 'rgba(239,83,80,0.5)',
    }));

    candleSeriesRef.current.setData(candleData);
    volumeSeriesRef.current.setData(volumeData);
    chartRef.current?.timeScale().fitContent();
  }, [candles]);

  const color = ohlcv?.isUp ? '#26a69a' : '#ef5350';

  return (
    <div style={{ position: 'relative', width: '100%', height: '100%' }}>
      <div ref={containerRef} style={{ width: '100%', height: '100%' }} />
      {ohlcv && (
        <div
          style={{
            position: 'absolute',
            top: 8,
            left: 12,
            display: 'flex',
            gap: 14,
            fontSize: 12,
            fontFamily: 'monospace',
            color: '#c1c2c5',
            pointerEvents: 'none',
            zIndex: 10,
            flexWrap: 'wrap',
          }}
        >
          <span>
            O <span style={{ color }}>{formatPrice(ohlcv.open)}</span>
          </span>
          <span>
            H <span style={{ color }}>{formatPrice(ohlcv.high)}</span>
          </span>
          <span>
            L <span style={{ color }}>{formatPrice(ohlcv.low)}</span>
          </span>
          <span>
            C <span style={{ color }}>{formatPrice(ohlcv.close)}</span>
          </span>
          <span style={{ color }}>
            {ohlcv.change >= 0 ? '+' : ''}
            {formatPrice(ohlcv.change)} ({ohlcv.changePercent >= 0 ? '+' : ''}
            {ohlcv.changePercent.toFixed(2)}%)
          </span>
          <span>
            Vol <span style={{ color }}>{formatVolume(ohlcv.volume)}</span>
          </span>
        </div>
      )}
    </div>
  );
}
