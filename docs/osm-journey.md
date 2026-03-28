# From Google to OpenStreetMap — How Walk My Way Reduced Its Vendor Dependencies

## Where It Started

Walk My Way began as a straightforward idea: help a friend reach his daily step goal by turning otherwise ordinary walks into purposeful routes. He wanted to get to a destination on foot but pass through useful places along the way — a café, a pharmacy, a park. Simple concept, reasonable scope.

The initial technical implementation was equally straightforward: lean on Google's APIs. Google Places for finding nearby POIs. Google Geocoding for turning GPS coordinates into readable addresses and for resolving destination names. Google Directions for building the actual route.

And it worked. The first version was fast to build, the data was excellent, and the integration was clean. Google's APIs are mature, well-documented, and return consistent results.

## The First Problem: Cost

The moment the app became publicly accessible, a new concern emerged: **cost**.

Google's APIs are priced per call. For a personal project with low traffic, the free tier is generous enough. But "publicly accessible" means anyone can use it — and if the app ever gained any traction, the bill could grow very quickly, entirely outside my control.

More uncomfortably: every autocomplete keystroke, every reverse geocoding request, every route calculation was a billed API call. The usage wasn't linear — it was amplified. A single route calculation involved multiple geocoding lookups, several Places API searches, and a Directions API call. The cost per user interaction was surprisingly high.

## The Second Problem: Security

Billing risk led directly to a security concern: **the app needed to be protected**.

An unprotected, publicly accessible API is an invitation for abuse — intentional or accidental. If anyone could call the route endpoint without restriction, a single bot could generate thousands of billed API calls in minutes.

The obvious solution was authentication. But proper authentication meant user accounts: registration, login, session management, password resets. That is significant complexity for a project whose entire premise is simplicity. I wanted to help a friend walk more, not build an identity platform.

The alternative — a shared API key on the frontend — doesn't meaningfully protect against a determined attacker, but it does require the full Google API key to be exposed client-side, which brings its own risks.

I was heading toward a difficult trade-off: complexity for security, or exposure for simplicity.

## Looking for Alternatives

At this point the question shifted: what if I could remove the Google dependency entirely for most of the functionality?

I looked at several options:

**Mapbox** — excellent product, well-designed APIs, solid free tier. But it is still a hosted service with per-call pricing, and the same fundamental exposure problem applies.

**MapAtlas** ([mapatlas.eu](https://mapatlas.eu/de)) — an interesting European alternative with privacy-focused positioning. Promising, but a smaller ecosystem.

**OpenStreetMap** — the obvious candidate. I had known about OSM for years but had always treated self-hosting as an advanced topic best left to infrastructure teams with dedicated resources. The system requirements — both for the database and for the import tooling — felt intimidating for a personal project.

## Reconsidering OpenStreetMap

What changed my mind was reconsidering the actual scope. I did not need to self-host the entire planet. I needed Austria.

Geofabrik publishes regularly updated extracts for individual countries and regions at [download.geofabrik.de](https://download.geofabrik.de/europe/austria.html). The Austria extract is a single `.osm.pbf` file — roughly 700 MB. Manageable.

The `.osm.pbf` format (Protocol Buffer Binary Format) is the standard binary encoding for raw OpenStreetMap data. It is compact and efficient, and it is what most OSM tooling speaks natively.

## First Attempt: OsmSharp

The .NET ecosystem has a well-maintained library for reading OSM data directly: [OsmSharp](https://www.osmsharp.com/).

I integrated it and tried loading the PBF file at application startup. The API was clean and the library did exactly what it advertised. But there was a fundamental mismatch: OsmSharp is designed for reading, transforming, and writing OSM files — not for serving spatial queries at low latency against a large dataset.

Reading the Austria extract into memory took a significant amount of time — long enough that it would cause unacceptable startup delays in a web application. The issue was [a known limitation of the format itself](https://github.com/OsmSharp/core/issues/97): PBF files are not indexed for random access, so loading data is inherently sequential.

More critically, there was no efficient way to answer "what cafés are within 500 meters of this point?" without either loading everything into memory or scanning the file repeatedly. Neither was acceptable.

## The Real Solution: osm2pgsql + PostgreSQL

Back to the drawing board — and the right answer was simpler than I expected.

[osm2pgsql](https://osm2pgsql.org/) is a purpose-built tool for importing OSM data into a PostgreSQL database with the PostGIS spatial extension. It reads the PBF file once, extracts the features you care about (points, polygons, road segments), and writes them into structured tables with proper spatial indexing.

The result is a standard PostgreSQL database that can answer spatial queries at full database speed:

- "Find the 5 nearest cafés to this point" — a single indexed query, milliseconds
- "Reverse geocode this coordinate" — a union over address points and polygons, fast
- "Autocomplete this partial address" — a trigram index query with spatial proximity ranking

Three of the four core geo features the app needs — reverse geocoding, address autocomplete, and POI lookup for route building — are now handled entirely by this local database. No external API call, no latency, no per-request cost.

Two Google dependencies remain, however. The actual route calculation — determining the optimal walking sequence through the selected stops — still uses the **Google Directions API**. Once the POI stops are resolved locally, the ordered waypoints are handed off to Directions to compute the walking route and resolve the final destination address. The result is then packaged into a **Google Maps deep-link** that the user opens on their device for turn-by-turn navigation. These two are tightly coupled: Directions computes the route, Maps consumes it.

## The Result

The architecture shift had a significant practical impact:

| Feature | Before | After |
|---------|--------|-------|
| Reverse geocoding | Google Geocoding API | Local PostGIS query |
| Address autocomplete | Google Places Autocomplete API | Local pg_trgm + PostGIS |
| POI search | Google Places Nearby API | Local PostGIS query |
| Route calculation | Google Directions API | **Google Directions API** (still) |
| Turn-by-turn navigation | Google Maps URL | **Google Maps URL** (still) |

**Google API calls per user interaction dropped from ~10-15 to 1** — the single Directions API call that calculates the final route. Everything else runs locally.

The authentication concern largely resolved itself as a side effect. With no sensitive third-party keys to protect beyond the one remaining Directions key, the only thing worth securing is the app's own API surface — which is handled with a simple shared key and short-lived JWT tokens, without needing user accounts.

## What I Learned

Self-hosting OSM is more approachable than its reputation suggests, at least at the country-extract level. The tooling (osm2pgsql, PostGIS) is mature, well-documented, and integrates naturally with a standard PostgreSQL stack.

The initial hesitation about system requirements was mostly unfounded for a country-sized dataset. An Austria-only import takes a few minutes and results in a database of a few gigabytes — well within the resources available on a modest VPS or container.

The deeper lesson is about the hidden costs of vendor dependence. Not just financial costs — though those are real — but the architectural constraints that follow from building on a metered, third-party API surface. Moving critical functionality in-house trades operational complexity for independence, and in this case, the trade was clearly worth it.

## What's Next

The two remaining Google dependencies are the natural next target. Route calculation via the Directions API is the last metered call per user interaction, and the Google Maps navigation link means the end-user experience is still tied to a Google product.

Viable alternatives exist. [OSRM](http://project-osrm.org/) (Open Source Routing Machine) and [Valhalla](https://github.com/valhalla/valhalla) are both open-source routing engines that can be self-hosted on top of OSM data — the same dataset already in the database. For navigation, [OpenRouteService](https://openrouteservice.org/) offers a free hosted option, and deep-links into Apple Maps or OsmAnd are feasible client-side replacements for the Google Maps URL.

The challenge is validation: route quality and edge-case handling matter a lot for a walking app. A self-hosted routing engine needs to produce results that are at least as reliable as Directions before it is worth the operational overhead of running another service. That investigation is future work — but the foundation is already in place.
