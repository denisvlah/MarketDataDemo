# MarketDataDemo UI — Frontend Application

A frontend Single Page Application (SPA) to visualize OHLCV candlestick and volume data stored in the MarketDataDemo API.

## Features

- **Interactive Candlestick & Volume Charts**: Powered by TradingView's Lightweight Charts (`lightweight-charts`) with responsive layout and crosshair tooltips.
- **Dynamic Symbol & Interval Discovery**: Automatically reads symbols and resolutions from the `/candles/files` endpoint on startup.
- **Date Range Selection**: Date and time pickers with intuitive range presets and validation.
- **Mantine UI Components**: Modern, accessible UI built with `@mantine/core` and `@tabler/icons-react`.
- **Azure Static Web App Deployment**: Production-ready SPA configuration including `staticwebapp.config.json` navigation fallbacks and security headers.

---

## Tech Stack

- **React 19** with **TypeScript**
- **Vite** build tooling
- **Mantine UI v9** (`@mantine/core`, `@mantine/dates`, `@mantine/hooks`)
- **TradingView Lightweight Charts v5** (`lightweight-charts`)
- **Day.js** for timestamp manipulations

---

## Configuration & Environment Variables

| Variable | Description | Local Dev Default | Production / Azure Default |
|---|---|---|---|
| `VITE_API_BASE_URL` | Base URL of the MarketDataDemo backend API | Empty string (uses Vite dev server proxy) | `https://<container-app-fqdn>` |

### Local Development (.env)

When running locally with `npm run dev`, Vite proxies `/candles` requests to the local backend API running on `http://localhost:5044`. You do **not** need to configure `VITE_API_BASE_URL` for local development.

```env
# Optional override for local development if targeting a remote backend:
# VITE_API_BASE_URL=http://localhost:5044
```

### Production (.env.production)

When deploying to Azure Static Web Apps, `VITE_API_BASE_URL` is set to the HTTPS URL of the deployed Azure Container App (e.g. `https://market-data-demo-api.<region>.azurecontainerapps.io`).

---

## CORS & API Connectivity

The backend API (`MarketDataDemo.Api`) and Azure Container App ingress are configured to allow Cross-Origin Resource Sharing (CORS):
- **Container App Ingress CORS**: The Terraform script configures the Container App ingress `cors` block to permit requests originating from `https://<static-web-app>.azurestaticapps.net` and local development origins.
- **ASP.NET Core API CORS**: The API includes CORS middleware (`AddCors` / `UseCors`) to handle preflight `OPTIONS` requests and send standard CORS response headers (`Access-Control-Allow-Origin: *`).

---

## Local Development & Build

### 1. Install Dependencies
```bash
npm install
```

### 2. Run Local Dev Server
```bash
npm run dev
```
Open [http://localhost:5173](http://localhost:5173) in your browser.

### 3. Build for Production
```bash
# Set backend API URL for production build:
export VITE_API_BASE_URL="https://market-data-demo-api.westeurope.azurecontainerapps.io"
npm run build
```
The compiled static assets are output to the `dist/` directory.

### 4. Preview Production Build
```bash
npm run preview
```

---

## Azure Static Web App Configuration (`staticwebapp.config.json`)

The application includes a `staticwebapp.config.json` configuration file located in `public/` (and compiled to `dist/`) with:
- **Navigation Fallback**: Rewrites unknown non-file requests to `/index.html` for client-side routing.
- **MIME Types**: Explicit `.json` MIME type mappings.
- **Content Security Policy**: Global headers allowing API connectivity to the Container App backend.

---

## Automated CI/CD Deployment

The GitHub Actions workflow at [`.github/workflows/deploy.yaml`](../.github/workflows/deploy.yaml) automatically:
1. Provisions the Azure Static Web App and Azure Container App via Terraform.
2. Extracts the Container App URL and Static Web App deployment token.
3. Builds the frontend with `VITE_API_BASE_URL=<container_app_url>`.
4. Deploys the static bundle to Azure Static Web Apps via `Azure/static-web-apps-deploy@v1`.
