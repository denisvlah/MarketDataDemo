import { Group, Select, Button } from '@mantine/core';
import { DateTimePicker } from '@mantine/dates';
import { IconRefresh } from '@tabler/icons-react';

const INTERVAL_LABELS: Record<number, string> = {
  1: '1m',
  5: '5m',
  15: '15m',
  30: '30m',
  60: '1h',
  240: '4h',
  1440: '1D',
  10080: '1W',
};

function formatInterval(minutes: number): string {
  return INTERVAL_LABELS[minutes] ?? `${minutes}m`;
}

interface Props {
  symbols: string[];
  intervals: number[];
  symbol: string;
  interval: number;
  from: string | null;
  to: string | null;
  loading: boolean;
  onSymbolChange: (s: string) => void;
  onIntervalChange: (i: number) => void;
  onFromChange: (d: string | null) => void;
  onToChange: (d: string | null) => void;
  onLoad: () => void;
}

export default function ChartToolbar({
  symbols,
  intervals,
  symbol,
  interval,
  from,
  to,
  loading,
  onSymbolChange,
  onIntervalChange,
  onFromChange,
  onToChange,
  onLoad,
}: Props) {
  return (
    <Group p="md" gap="sm" wrap="wrap">
      <Select
        label="Symbol"
        searchable
        data={symbols}
        value={symbol}
        onChange={(v) => v && onSymbolChange(v)}
        w={160}
      />
      <Select
        label="Interval"
        data={intervals.map((i) => ({ value: String(i), label: formatInterval(i) }))}
        value={String(interval)}
        onChange={(v) => v && onIntervalChange(Number(v))}
        w={100}
      />
      <DateTimePicker
        label="From"
        value={from}
        onChange={onFromChange}
        w={220}
      />
      <DateTimePicker
        label="To"
        value={to}
        onChange={onToChange}
        w={220}
      />
      <Button
        mt={24}
        leftSection={<IconRefresh size={16} />}
        loading={loading}
        onClick={onLoad}
      >
        Load
      </Button>
    </Group>
  );
}
