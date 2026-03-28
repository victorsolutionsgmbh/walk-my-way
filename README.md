# Walk My Way

**Live:** [https://walkmyway.fr](https://walkmyway.fr)

[![Docker Image CI](https://github.com/victorsolutionsgmbh/walk-my-way/actions/workflows/docker-ci.yml/badge.svg)](https://github.com/victorsolutionsgmbh/walk-my-way/actions/workflows/docker-ci.yml)

## About

A friend of mine wanted to change his habits: unhappy with his weight, he set himself a daily goal of 10,000 steps. The challenge was not the walking itself — it was making the walks purposeful. Instead of wandering aimlessly, he wanted to reach actual destinations while passing by places worth visiting along the way: a café for a morning coffee, a pharmacy to pick up a prescription, a park to sit for a minute.

**Walk My Way** was built to solve exactly that. It is a web application that takes your current GPS position and a destination, lets you select points of interest (POIs) you want to pass through, and produces a route you can open directly in Google Maps for turn-by-turn navigation.

The app is intentionally simple: no accounts, no history, no tracking. You open it, tell it where you are going, pick your stops, and start walking.

---

## Features

- **Address autocomplete** — type a destination and get real-time suggestions based on your proximity
- **Reverse geocoding** — automatically resolves your GPS coordinates to a readable address
- **POI stop selection** — add up to 4 intermediate stops (cafés, parks, restaurants, pharmacies, supermarkets, and more)
- **Ordered or optimized routing** — optionally fix the order of your stops or let the app find the most efficient sequence
- **Open-now filter** — filter POIs to only those currently open
- **Google Maps deep-link** — the result opens directly in Google Maps with all waypoints pre-filled
- **Region restriction** — restricted to Austria (Cloudflare header-based check)
- **German / English** — full i18n support
- **Mobile-first** — tested on iOS Safari and Android Chrome, with touch drag-and-drop for stop reordering

---

## Tech Stack

### Backend
- **ASP.NET Core 8** — REST API
- **PostgreSQL 15+ with PostGIS** — spatial data storage and querying
- **OpenStreetMap data** — loaded via [osm2pgsql](https://osm2pgsql.org/) from the [Geofabrik Austria extract](https://download.geofabrik.de/europe/austria.html)
- **Npgsql** — PostgreSQL driver with connection pooling

### Frontend
- **React 18** — single-page application
- **Vite** — build tooling
- Served as static files from the ASP.NET Core host (no separate frontend server)

### Infrastructure
- **Docker** — multi-stage build (SDK Alpine + ASP.NET Debian runtime with osm2pgsql)
---

## Architecture Overview

```
Browser (React SPA)
  │
  │  POST /api/auth  →  returns JWT (15 min)
  │  GET/POST /api/route/*  →  requires Bearer token
  ▼
ASP.NET Core 8
  │
  ├── RouteController
  │     ├── GET  /api/route/autocomplete   → address suggestions
  │     ├── GET  /api/route/address        → reverse geocode
  │     ├── GET  /api/route/check-region   → Cloudflare country check
  │     └── POST /api/route               → find walking route with POI stops
  │
  └── PostgresOsmProvider
        └── PostgreSQL + PostGIS
              └── OSM data (Austria)
```

On application startup, `OsmDatabaseInitializer` automatically:
1. Checks whether the `osm` database exists; creates it if missing
2. Ensures PostGIS, hstore, and pg_trgm extensions are installed
3. Downloads the Austria OSM PBF from Geofabrik (if not yet imported)
4. Runs `osm2pgsql` to import the data
5. Creates spatial and trigram indexes

---

## Getting Started

### Prerequisites

| Tool | Version |
|------|---------|
| .NET SDK | 8.0+ |
| Node.js | 20+ |
| PostgreSQL | 15+ with PostGIS |
| osm2pgsql | any recent |

### Configuration

The following settings must be provided (via `appsettings.json`, environment variables, or user secrets):

| Key | Description |
|-----|-------------|
| `ConnectionStrings:Osm` | PostgreSQL connection string |
| `Auth:ApiKey` | Static key the frontend sends to obtain a JWT |
| `Auth:JwtSecret` | Signing secret for JWT (min. 32 characters) |
| `VITE_API_KEY` | Must match `Auth:ApiKey`; set as Vite env variable at build time |

For local development, `appsettings.Development.json` contains placeholder values for all of the above.

### Running Locally

```bash
# from repo root
dotnet run --project WalkMyWay.Server
```

Vite dev server starts automatically via the SPA proxy. The first run will trigger the OSM database initialization (which downloads and imports ~700 MB of data — this takes several minutes).

### Docker

```bash
docker build -t walk-my-way-server .
docker run -e ConnectionStrings__Osm="..." \
           -e Auth__ApiKey="your-key" \
           -e Auth__JwtSecret="your-32-char-secret" \
           -e VITE_API_KEY="your-key" \
           -p 8080:8080 \
           walk-my-way-server
```

> **Note:** Pass `VITE_API_KEY` as a build argument if baking it into the image: `docker build --build-arg VITE_API_KEY=your-key .`

---

## Authentication Flow

All API endpoints (except `POST /api/auth` itself) require a valid JWT.

```
1. App starts → frontend calls POST /api/auth with { apiKey: VITE_API_KEY }
2. Backend validates the key → returns a signed JWT (15-minute expiry)
3. Frontend stores the token in memory
4. Every API call includes  Authorization: Bearer <token>
5. When the token is within 30 seconds of expiry, the frontend transparently
   re-authenticates before the next request — no user interaction required
```

The static API key is intentionally simple — it prevents casual scraping without requiring user accounts or complex infrastructure.

---

## Background

Read about the journey from Google APIs to a fully self-hosted OSM stack:
[From Google to OpenStreetMap — How Walk My Way Reduced Its Vendor Dependencies](docs/osm-journey.md)
