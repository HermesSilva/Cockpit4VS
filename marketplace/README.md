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

The listing uses the family's shared screenshots. They show **only the Cockpit panel**, with
no editor chrome around it, and the panel is the same React bundle in both IDEs — so the
same image is true of both listings. They are captured from the real webview bundle by the
harness in the base repo (`Cockpit/scripts/screenshots/`), not drawn by hand.

What still must not appear here is a picture of a *host's own* UI. `Settings View.png` is VS
Code's settings page; it stays out of this listing.

**The image folder is shared, and it lives in the base repo:
`Cockpit/images/`.** There is one copy of each screenshot for the whole family; this repo
does not keep its own. Markdown here references them by absolute URL:

```markdown
![Hub](https://raw.githubusercontent.com/HermesSilva/Cockpit/master/images/Main%20Panel.png)
```

A relative path cannot work — the file is in a sibling repository, so it resolves to
nothing on GitHub and nothing on the Marketplace. The absolute URL renders in both.

An image that only makes sense for Visual Studio goes into `Cockpit/images/` with a name
that marks the host (e.g. `Main Panel VS.png`), so the two sets never get confused, and is
referenced from `overview.md` by absolute URL as above. Note the URLs point at the base
repo's `master`: a new image renders only after it is merged there.

`publishManifest.json` `assetFiles` is **not** a route for this: `pathOnDisk` is resolved
inside this repository, so it cannot reach the base repo's folder. Use it only for assets
that genuinely live here.
