# Settings schema — `<ModDll>.settings.json`

The mod manager can edit **any** MelonLoader mod's settings in-game, with no code written for
that specific mod. There are two levels of support, and you get the first one for free.

## Level 1 — do nothing (automatic)

If your mod writes a config file at `UserData/<YourAssemblyName>.ini` in plain `key=value`
form, the manager discovers it, infers each type, and renders editable controls.

```ini
# BAPBAPHpNumbers configuration
EnableHpNumbers=True
ShowFormat=HpOverMax
ShowForAllHpBars=True
```

Type inference:

| Value looks like | Control | Notes |
|---|---|---|
| `True` / `False` (any case) | Toggle switch | |
| A number | Slider | Range derived from the current value — see below |
| Anything else | Read-only text | The manager will not guess at a string's valid values |

Keys are humanised for display: `EnableHpNumbers` becomes "Enable hp numbers". Lines starting
with `#` and lines without an `=` are skipped.

Because there is no range information in an ini, the slider bounds are derived from the value
currently on disk: a value ≤ 1 gets a 0–2 range, ≤ 10 gets 0–20, ≤ 100 gets 0–200, and anything
larger gets 0–(2× the value). That is a guess. If it matters, use level 2.

## Level 2 — ship a descriptor (recommended)

Drop `UserData/<YourAssemblyName>.settings.json` next to your ini. **If a descriptor exists it
replaces inference entirely** — only the keys you list appear, in the order you list them, so
this also lets you hide internal keys and control ordering.

The descriptor describes your ini; it does not store values. Values are always read from and
written back to `UserData/<YourAssemblyName>.ini`.

```json
[
  {
    "key": "SpawnMultiplier",
    "label": "Spawn rate",
    "description": "Multiplies how many enemies spawn. 1.0 is the game default.",
    "type": "float",
    "min": 0.5,
    "max": 4.0,
    "step": 0.1,
    "scope": "host"
  },
  {
    "key": "ShowTimer",
    "label": "Show round timer",
    "description": "Draws a countdown in the corner of your screen.",
    "type": "bool",
    "scope": "client"
  },
  {
    "key": "DisplayFormat",
    "label": "Timer format",
    "type": "text",
    "scope": "client"
  }
]
```

### Fields

| Field | Required | Type | Default | Meaning |
|---|---|---|---|---|
| `key` | **yes** | string | — | The ini key this controls. Entries without a `key` are skipped. |
| `label` | no | string | the key | Shown in the UI. |
| `description` | no | string | `""` | One-line help under the control. |
| `type` | no | `bool` \| `text` \| `float` | `float` | Anything unrecognised is treated as `float`. |
| `min` | no | number | `0` | `float` only. |
| `max` | no | number | `100` | `float` only. |
| `step` | no | number | `(max-min)/20`, floor `0.05` | `float` only. |
| `scope` | no | `client` \| `host` | `client` | See below. Anything other than `host` reads as `client`. |

`text` settings are displayed read-only — the manager will not invent a keyboard for a free-form
string. Use `bool` or `float` for anything you want changed in-game.

### `scope`

- **`client`** — a local preference. Affects only the machine it is set on. Always editable.
- **`host`** — read by whoever is hosting, and therefore changes the match for everyone in the
  lobby. The manager locks these while a match is in progress, and fails safe (treats the state
  as locked) when it cannot determine whether a match is running.

Mark anything that changes gameplay for other players as `host`. Getting this wrong is the one
way a descriptor can cause a bad experience for someone else in the lobby.

### Parser notes

The manager uses a small hand-rolled JSON reader rather than pulling a JSON library into an
IL2CPP mod. It is deliberately forgiving, but keep to the shape above:

- A flat array of flat objects. **Nested objects inside an entry will break that entry's parsing**
  — the reader splits on brace depth and reads the first match for each field name.
- Values may be quoted strings or bare numbers/booleans.
- Field names are matched case-insensitively.
- If the file cannot be read or parsed, the manager falls back to ini inference rather than
  showing an error.

## What the manager cannot infer

The **mod-level** host/client classification — whether the mod as a whole affects the lobby — is
not read from the descriptor. It comes from a small metadata overlay inside the manager
(`ModCatalog.Known`). A mod not in that overlay still appears and is still toggleable, but is
listed under **Unrecognised** and flagged as unknown scope. Per-setting `scope` in your
descriptor is independent of this and does work.

## Where files go

```
<BAPBAP>/UserData/
    YourMod.ini             <- your values (you own this file)
    YourMod.settings.json   <- your descriptor (optional)
```

The stem must match your **assembly name** (the DLL filename without `.dll`), not your
`MelonInfo` display name.

## Changes take effect

Setting edits are written straight to the ini. Whether they apply immediately depends on your
mod — if you re-read the ini periodically, they will. Enabling or disabling a *mod* always
requires a restart, because MelonLoader cannot unload an assembly mid-session.
