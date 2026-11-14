# Jawbreakers Guild

Website for Jawbreakers, a Horde guild on the EU PvP ruleset of World of Warcraft: Forever.

- **Guild members**: one list per class, with links to each character's Armory page and a description per character
- **Events**: date, time, starting point in game, description and requirements
- **Crafting**: each profession with the characters that have it, a button to its leveling guide on wow-professions.com,
  and its recipes with materials, Wowhead tooltips and the characters who can make each one
- **About**: guild text and a history line of achievements
- **Admin** (`/admin.html`): add and edit events, characters (with their profession skill levels), the About text and the history.
  Signed in, the Crafting page also lets you tick who knows a recipe that doesn't come from a trainer

ASP.NET Core 8 minimal API, SQLite (EF Core), plain HTML/CSS/JS in `wwwroot`. No build step for the frontend.

## Run locally

```
dotnet run --environment Development
```

Without an admin password set, a development run makes a temporary one and prints it in the log.
Set a fixed one with the `Admin__Password` environment variable. Outside development, admin sign-in is off until it is set.

The database is created in `data/` on first start, and the member lists are filled from `seed/roster.json`
(taken from the official Armory on 2026-11-14) when they are empty.

Profession skill levels are filled the same way from `seed/professions.json` (also from the Armory, 2026-11-14).

## Crafting data

`wwwroot/recipes/<profession>.json` holds every recipe of a profession: spell id, name, required skill, source, what it makes and the
materials, plus the names and icons of the items involved. Taken from Wowhead's WoW: Forever database on 2026-11-14 (2,215 recipes);
the page links each recipe and material to Wowhead, whose `power.js` script shows the tooltips.

Who can make a recipe: for a trainer recipe (or one without a listed source, such as the starting recipes), everyone with the
profession and enough skill. For a recipe from a drop, vendor or quest, only the characters the admin has ticked.

## Settings

`appsettings.json` → `Guild`: the class lists, the races, the requirement checkboxes on the admin page (`EventRequirements`),
and the professions with their guide links and recipe files (`Professions`).

## Deploy

Live at https://lundqwist.se/jawbreakers/. The lundqwist.se site (github.com/Lundqwist/Website2.0) passes `/jawbreakers/...` on to this one,
which runs with `PathBase=/jawbreakers`. All addresses in `wwwroot` are relative so the pages work both there and at the root locally.

`.\deploy.ps1` publishes to `Z:\jawbreakers\app` on the NAS; `deploy\docker-compose.yml` runs it there on `127.0.0.1:8095`.
The admin password goes in a `.env` file next to the compose file (see `deploy\.env.example`).
