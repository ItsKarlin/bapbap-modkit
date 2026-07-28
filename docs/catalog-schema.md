# Catalog schema

What the in-game downloader reads to list, install and update mods.

This is **not** `dist/manifest.json`. That file is the installer payload — the manager itself,
what `install.sh` writes on a fresh machine. The catalog is the list of mods you can browse and
install *from inside* the manager. Keep them separate; a mod must never end up in the installer.

The layout deliberately mirrors [BAPHub's](https://github.com/Sonic0810/BAPBAPLauncher)
`manifest/channels/release/`, so one parser reads both sources and our packages could be
published through their launcher unchanged.

## Two levels, on purpose

Browsing costs **one** request. Installing costs **one more**.

```
catalog/catalog.json                          the browse list — everything the UI needs
catalog/<package-id>/<version>/version.json   the file list + hashes, fetched on Install
```

Hashes live only in `version.json`, so listing 50 mods doesn't mean downloading 50 manifests.

## catalog.json

```json
{
  "schemaVersion": 1,
  "sourceId": "modkit",
  "displayName": "Modkit",
  "baseUrl": "https://raw.githubusercontent.com/ItsKarlin/bapbap-modkit/main/",
  "packages": [
    {
      "id": "itskarlin.bapbap.third-person",
      "name": "BAPBAP Third Person",
      "summary": "Third-person camera on F1, with a pointer for card picks.",
      "author": "ItsKarlin",
      "scope": "client",
      "latestVersion": "1.0.1",
      "tags": ["camera", "client"],
      "versionManifestPath": "catalog/itskarlin.bapbap.third-person/1.0.1/version.json"
    }
  ]
}
```

| Field | Required | Meaning |
|---|---|---|
| `sourceId` | yes | Stable id for this source. Used to tell sources apart when several are merged. |
| `baseUrl` | yes | Everything else resolves against this. Must end with `/`. |
| `id` | yes | Globally unique, reverse-DNS: `<author>.bapbap.<mod>`. Survives file renames. |
| `name` | yes | Should match the mod's `MelonInfo` name, so an installed mod matches its catalog entry. |
| `scope` | no | `host` \| `client`. Absent means unknown, and the UI says so rather than guessing. |
| `latestVersion` | yes | Compared against the installed version to offer Update. |
| `versionManifestPath` | yes | Relative to `baseUrl`. |
| `summary`, `author`, `tags` | no | Display only. |

`scope` is ours; BAPHub has no equivalent. Packages from their catalog come through as unknown
scope and are listed under **Unrecognised**, exactly like an unrecognised installed mod.

## version.json

```json
{
  "schemaVersion": 1,
  "id": "itskarlin.bapbap.third-person",
  "version": "1.0.1",
  "files": [
    {
      "sourcePath": "dist/BAPBAPThirdPerson.dll",
      "targetPath": "Mods/BAPBAPThirdPerson.dll",
      "sha256": "72f9a2af…",
      "description": "Main mod dll"
    }
  ]
}
```

`sourcePath` resolves against `baseUrl`; `targetPath` is relative to the game folder and **must**
stay inside it — reject any path containing `..` or a drive/root prefix before writing. A catalog
is remote data, so treat it as untrusted.

Every file is sha256-verified before anything is written, and all files in a package are staged
and verified before *any* are moved into place, so a failed download can't leave a half-installed
mod.

## Requirements

BAPHub packages carry a `requirements[]` array, and we read it rather than inventing our own:

```json
"requirements": [
  { "id": "...", "type": "melonloader_version", "value": "0.7.2-ci.2388",
    "text": "MelonLoader 0.7.2-ci.2388 is required for managed installs.",
    "severity": "warning" }
]
```

Render `text` verbatim, styled by `severity`, and never hardcode a rule per mod. This is how the
Boss Rush mods — which need a different game build — get shown with a clear warning instead of
being hidden or silently installed into a build they don't work on.

## Uninstall

Removing a mod deletes the files listed in its `version.json` and nothing else. Its
`UserData/<Mod>.ini` is **kept by default**, so reinstalling restores your settings; deleting it
is a separate, explicit choice. Never delete a user's settings as a side effect.

## Merging sources

Sources are merged by `id`. If the same `id` appears twice, the first source listed wins and the
collision is logged. A catalog entry is matched to an installed mod by `name` against `MelonInfo`,
which is the same identity the manager already uses everywhere else.
