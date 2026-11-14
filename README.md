# Jawbreakers Guild

Website for Jawbreakers, a Horde guild on the EU PvP ruleset of World of Warcraft: Forever.

- **Guild members**: one list per class, with links to each character's Armory page and a description per character
- **Events**: date, time, starting point in game, description and requirements
- **About**: guild text and a history line of achievements
- **Admin** (`/admin.html`): add and edit events, characters, the About text and the history

ASP.NET Core 8 minimal API, SQLite (EF Core), plain HTML/CSS/JS in `wwwroot`. No build step for the frontend.

## Run locally

```
dotnet run --environment Development
```

Without an admin password set, a development run makes a temporary one and prints it in the log.
Set a fixed one with the `Admin__Password` environment variable. Outside development, admin sign-in is off until it is set.

The database is created in `data/` on first start, and the member lists are filled from `seed/roster.json`
(taken from the official Armory on 2026-11-14) when they are empty.

## Settings

`appsettings.json` → `Guild`: the class lists, the races, and the requirement checkboxes on the admin page (`EventRequirements`).

## Deploy

Live at https://lundqwist.se/jawbreakers/. The lundqwist.se site (github.com/Lundqwist/Website2.0) passes `/jawbreakers/...` on to this one,
which runs with `PathBase=/jawbreakers`. All addresses in `wwwroot` are relative so the pages work both there and at the root locally.

`.\deploy.ps1` publishes to `Z:\jawbreakers\app` on the NAS; `deploy\docker-compose.yml` runs it there on `127.0.0.1:8095`.
The admin password goes in a `.env` file next to the compose file (see `deploy\.env.example`).
