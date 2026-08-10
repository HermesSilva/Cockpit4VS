# Marketplace assets

What the Visual Studio Marketplace listing is made of, and what publishing it does.

| File | Role |
|---|---|
| `overview.md` | The listing page. Written for someone deciding whether to install, not for someone building the extension — that is what the repository `README.md` is for. |
| `publishManifest.json` | Everything the Marketplace needs that the VSIX manifest does not already say: publisher, categories, price, Q&A, repository. |

Everything else — display name, description, version, icon, tags, licence, supported
Visual Studio versions — is read from `src/Tootega.Cockpit/source.extension.vsixmanifest`.
There is one statement of each fact, and this folder is not where most of them live.

## Publishing

```powershell
./scripts/publish.ps1 -Vsix Dist\Tootega.Cockpit.vsix -PersonalAccessToken $env:VS_MARKETPLACE_PAT
```

The script wraps `VsixPublisher.exe`, which ships with the VS SDK. It refuses to publish a
`.vsix` whose version already exists unless you ask for it: the Marketplace overwrites in
place, and overwriting a released version is how a user ends up with two different builds
calling themselves the same thing.

A **personal access token** is required, scoped to *Marketplace (Publish)*, created at
<https://dev.azure.com> under the account that owns the publisher. Do not commit it; pass it
per run or keep it in `VS_MARKETPLACE_PAT`.

## Before the first publish

- The publisher (`HermesSilva`) must exist at
  <https://marketplace.visualstudio.com/manage/publishers>.
- **The `Publisher` attribute in `source.extension.vsixmanifest` must be the publisher's
  *display name* on the Marketplace, character for character** — here, `Hermes Silva`. The
  `publisher` field in `publishManifest.json` is the *identifier* (`HermesSilva`), which is
  a different string, and the two are checked against each other at upload. A mismatch is
  rejected with "Publisher display name … and Author name … need to be the same", which
  reads as if it wants the company name and does not.
- `categories` must name between one and three Marketplace categories. The Marketplace
  validates them at publish time and rejects the upload if one is unknown.
- `internalName` is the identifier in the listing URL. It is fixed after the first publish —
  changing it later creates a *second* extension rather than renaming the first.

## Screenshots

The listing has none yet, on purpose: the only screenshots in the family were taken in VS
Code, and showing VS Code chrome to sell a Visual Studio extension would misrepresent it.

To add them, put the images in `marketplace/images/`, reference them from `overview.md` with
a relative path, and declare each one in `publishManifest.json`:

```json
"assetFiles": [
  { "pathOnDisk": "images/hub.png", "targetPath": "images/hub.png" }
]
```
