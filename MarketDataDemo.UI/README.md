# MarketDataDemo UI — Requirements

A frontend SPA to display OHLCV candlestick charts for data stored in the MarketDataDemo API.

## 1. Tech Stack
1.1) React 18+ with TypeScript, scaffolded with **Vite**.
1.2) **Mantine UI v7** as the component library (free, polished, great date pickers and select components).
1.3) **TradingView Lightweight Charts v4** (`lightweight-charts`) for candlestick + volume visualization.

## 2. Data Source & API Integration
2.1) The UI connects to the ASP.NET API (default `http://localhost:5044`). The base URL should be configurable via an environment variable (`VITE_API_BASE_URL`).
2.2) On startup, call `GET /candles/files` to discover all available **symbols** and **intervals** and populate the dropdowns dynamically — no hardcoded symbol lists.
2.3) Fetch candle data via `GET /candles/{intervalMinutes}?symbol={symbol}&from={iso}&to={iso}`. The response is a JSON array of `{ t, o, h, l, c, v, n }`.

## 3. User Controls (toolbar / header bar)
3.1) **Symbol selector** — searchable dropdown (Mantine `Select` with `searchable`), populated from the `/candles/files` response (distinct symbols).
3.2) **Interval selector** — dropdown populated from available intervals for the selected symbol (e.g., 1, 5, 15, 60, 1440 minutes). Display human-friendly labels (e.g., "1m", "5m", "15m", "1h", "1D").
3.3) **Date range picker** — Mantine `DateTimePicker` pair (from / to) with sensible defaults: `from` = earliest available data start, `to` = latest available data end (derived from `/candles/files`).
3.4) **Load / Refresh button** to fetch and render the selected data.

## 4. Chart
4.1) Main pane: **Candlestick series** (OHLC) using TradingView Lightweight Charts.
4.2) Secondary pane (below): **Volume histogram** rendered as a histogram series, colored green/red based on close vs open.
4.3) Chart should be responsive and fill the available width. Minimum height ~500px.
4.4) Enable crosshair with OHLCV tooltip showing values at the hovered candle.
4.5) Chart time axis should display in **UTC**.

## 5. UX & State Management
5.1) Show a **loading spinner/skeleton** while data is being fetched.
5.2) Show a clear **error message** if the API is unreachable or returns an error.
5.3) Show an **empty state** message when no data exists for the selected parameters.
5.4) On first load, default to **symbol = "BTCUSD"**, **interval = 1440 (1D)**, **from = 2025-01-01**, **to = 2026-01-01** and immediately load the chart. If the user changes any control, fetch fresh data on demand.

## 6. Project Structure
6.1) Keep a clean component separation: `App`, `ChartToolbar`, `CandleChart`, `api/` service layer.
6.2) API calls in a dedicated `src/api/candlesApi.ts` module using `fetch`.
6.3) No global state library needed — React state + prop drilling is sufficient for this scope.

## 7. Build & Run
7.1) `npm run dev` for local development with hot reload.
7.2) `npm run build` produces a static bundle suitable for serving from any static host or Docker container.
7.3) In development mode, Vite's dev server **must** proxy all `/candles` requests to the API backend (default `http://localhost:5044`). No CORS configuration on the backend is needed — the proxy handles it. Configure this in `vite.config.ts` under `server.proxy`.

# React + TypeScript + Vite

This template provides a minimal setup to get React working in Vite with HMR and some ESLint rules.

Currently, two official plugins are available:

- [@vitejs/plugin-react](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react) uses [Oxc](https://oxc.rs)
- [@vitejs/plugin-react-swc](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react-swc) uses [SWC](https://swc.rs/)

## React Compiler

The React Compiler is not enabled on this template because of its impact on dev & build performances. To add it, see [this documentation](https://react.dev/learn/react-compiler/installation).

## Expanding the ESLint configuration

If you are developing a production application, we recommend updating the configuration to enable type-aware lint rules:

```js
export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      // Other configs...

      // Remove tseslint.configs.recommended and replace with this
      tseslint.configs.recommendedTypeChecked,
      // Alternatively, use this for stricter rules
      tseslint.configs.strictTypeChecked,
      // Optionally, add this for stylistic rules
      tseslint.configs.stylisticTypeChecked,

      // Other configs...
    ],
    languageOptions: {
      parserOptions: {
        project: ['./tsconfig.node.json', './tsconfig.app.json'],
        tsconfigRootDir: import.meta.dirname,
      },
      // other options...
    },
  },
])
```

You can also install [eslint-plugin-react-x](https://github.com/Rel1cx/eslint-react/tree/main/packages/plugins/eslint-plugin-react-x) and [eslint-plugin-react-dom](https://github.com/Rel1cx/eslint-react/tree/main/packages/plugins/eslint-plugin-react-dom) for React-specific lint rules:

```js
// eslint.config.js
import reactX from 'eslint-plugin-react-x'
import reactDom from 'eslint-plugin-react-dom'

export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      // Other configs...
      // Enable lint rules for React
      reactX.configs['recommended-typescript'],
      // Enable lint rules for React DOM
      reactDom.configs.recommended,
    ],
    languageOptions: {
      parserOptions: {
        project: ['./tsconfig.node.json', './tsconfig.app.json'],
        tsconfigRootDir: import.meta.dirname,
      },
      // other options...
    },
  },
])
```
