# Configurable Textless Priority Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users rank textless (no-language-tag) images above titled art per image type, and document the image-picker language-tag trade-off so it stops being reported as a bug.

**Architecture:** Add three `PreferNoLanguageFor*` bool config fields (default `false`). In the base provider, the per-type `textlessRank` becomes three-state: excluded (`int.MaxValue`), last (`buckets.Count`, current behaviour), or top (`-1`, new). Ranks are pure sort keys, so `-1` sorts first and the existing synthetic-rating encoding needs no change. Admin UI, README, CLAUDE.md, and version are updated in lockstep.

**Tech Stack:** .NET 8, C#, TMDbLib, Jellyfin `IRemoteImageProvider`, embedded HTML config page (emby-checkbox web components), no automated test framework.

## Global Constraints

- **No automated tests.** Per `CLAUDE.md`, verification is manual (build, install, restart Jellyfin, refresh an item). Each code task is gated by `dotnet build` succeeding; one manual end-to-end check runs at the end.
- **Backward compatible defaults.** All three new bools default `false`, keeping today's dead-last textless behaviour for existing configs. No property is renamed or retyped (avoids XML-config migration).
- **No new dependencies.** Only two assemblies ship (`Jellyfin.Plugin.LanguageAwareImages.dll`, `TMDbLib.dll`).
- **Version consistency.** The four-part version `X.Y.Z.W` and `targetAbi` (`10.10.0.0`, unchanged) must match across the `.csproj` and `meta.json`. `manifest.json` is machine-edited by CI on tag push, never by hand.
- **Config in three places in lockstep:** `PluginConfiguration.cs`, `Configuration/configPage.html`, README config table.
- **Writing style:** English in code and docs, no em-dash characters.

---

### Task 1: Config fields + three-state textless ranking

**Files:**
- Modify: `Configuration/PluginConfiguration.cs:24` (after the `IncludeNoLanguageForLogos` field)
- Modify: `Providers/LanguageAwareImageProviderBase.cs:133` (after `IsTextlessAllowedFor`) and `:245` (the `textlessRank` line)

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `bool PluginConfiguration.PreferNoLanguageForPosters` (default `false`)
  - `bool PluginConfiguration.PreferNoLanguageForBackdrops` (default `false`)
  - `bool PluginConfiguration.PreferNoLanguageForLogos` (default `false`)
  - `static bool LanguageAwareImageProviderBase.IsTextlessPreferredFor(ImageType type)`

- [ ] **Step 1: Add the three config fields**

In `Configuration/PluginConfiguration.cs`, immediately after the
`IncludeNoLanguageForLogos` property (line 24), add:

```csharp

    // When the matching IncludeNoLanguageFor* toggle is on, rank textless
    // (no-language-tag) images at the very top of the bucket order (above the
    // preferred language) instead of dead-last. Off (default) keeps textless as
    // a last resort. Has no effect while the matching Include toggle is off,
    // because textless is then never even requested from TMDB. Common use:
    // clean backdrops/logos without burned-in text.
    public bool PreferNoLanguageForPosters { get; set; } = false;

    public bool PreferNoLanguageForBackdrops { get; set; } = false;

    public bool PreferNoLanguageForLogos { get; set; } = false;
```

- [ ] **Step 2: Add the `IsTextlessPreferredFor` helper**

In `Providers/LanguageAwareImageProviderBase.cs`, immediately after the closing
brace of `IsTextlessAllowedFor` (line 133), add:

```csharp

    protected static bool IsTextlessPreferredFor(ImageType type) => type switch
    {
        ImageType.Primary => Config.PreferNoLanguageForPosters,
        ImageType.Backdrop => Config.PreferNoLanguageForBackdrops,
        ImageType.Logo => Config.PreferNoLanguageForLogos,
        _ => false
    };
```

- [ ] **Step 3: Make `textlessRank` three-state**

In `Providers/LanguageAwareImageProviderBase.cs`, replace the single line inside
`FetchRankMapAsync` (line 245):

```csharp
            var textlessRank = IsTextlessAllowedFor(type) ? buckets.Count : int.MaxValue;
```

with:

```csharp
            int textlessRank;
            if (!IsTextlessAllowedFor(type))
            {
                textlessRank = int.MaxValue; // excluded for this type
            }
            else if (IsTextlessPreferredFor(type))
            {
                textlessRank = -1; // above every language bucket
            }
            else
            {
                textlessRank = buckets.Count; // dead-last (current behaviour)
            }
```

Do NOT touch `BuildRemoteImageInfo`: `MergeAndRank` sorts by `OrderBy(r => r.Rank)`
so `-1` sorts before `0` naturally, and `rating = maxRank + 1 - rank` yields
`buckets.Count + 2` for rank `-1` (the highest value), so the order also survives
Jellyfin's downstream `CommunityRating` re-sort.

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `Build succeeded`, 0 errors. (A warning-free build is expected; the new
switch arms cover all supported `ImageType` values with a `_ => false` default.)

- [ ] **Step 5: Commit**

```bash
git add Configuration/PluginConfiguration.cs Providers/LanguageAwareImageProviderBase.cs
git commit -m "feat: rank textless images first per type when preferred (#3)"
```

---

### Task 2: Admin config page (checkboxes + load/save + disable-when-not-allowed)

**Files:**
- Modify: `Configuration/configPage.html` (the "Allow textless" `verticalSection` at lines 82-105, the load block ~245, the save block ~270, and the script IIFE head ~232)

**Interfaces:**
- Consumes: the three `PreferNoLanguageFor*` config fields from Task 1.
- Produces: no code interface (UI only).

- [ ] **Step 1: Replace the textless section markup**

In `Configuration/configPage.html`, replace the whole `verticalSection` block
that currently spans lines 82-105 (header "Allow textless (no language tag)
images" through the three simple `Posters`/`Backdrops`/`Logos` checkboxes) with:

```html
                <div class="verticalSection">
                    <h3 class="checkboxListLabel">Textless (no language tag) images</h3>
                    <p style="margin-top: 0.5em">Per image type, since logos and backdrops usually
                       work fine textless but posters generally don't. Enable "Prefer" to rank the
                       textless version <em>above</em> titled art when one exists (only takes effect
                       while "Allow" is on for that type).</p>

                    <div class="checkboxContainer">
                        <label>
                            <input is="emby-checkbox" type="checkbox" id="IncludeNoLanguageForPosters" />
                            <span>Allow textless posters</span>
                        </label>
                    </div>
                    <div class="checkboxContainer" style="margin-left: 2.5em">
                        <label>
                            <input is="emby-checkbox" type="checkbox" id="PreferNoLanguageForPosters" />
                            <span>Prefer textless posters over titled</span>
                        </label>
                    </div>

                    <div class="checkboxContainer">
                        <label>
                            <input is="emby-checkbox" type="checkbox" id="IncludeNoLanguageForBackdrops" />
                            <span>Allow textless backdrops</span>
                        </label>
                    </div>
                    <div class="checkboxContainer" style="margin-left: 2.5em">
                        <label>
                            <input is="emby-checkbox" type="checkbox" id="PreferNoLanguageForBackdrops" />
                            <span>Prefer textless backdrops over titled</span>
                        </label>
                    </div>

                    <div class="checkboxContainer">
                        <label>
                            <input is="emby-checkbox" type="checkbox" id="IncludeNoLanguageForLogos" />
                            <span>Allow textless logos</span>
                        </label>
                    </div>
                    <div class="checkboxContainer" style="margin-left: 2.5em">
                        <label>
                            <input is="emby-checkbox" type="checkbox" id="PreferNoLanguageForLogos" />
                            <span>Prefer textless logos over titled</span>
                        </label>
                    </div>
                </div>
```

- [ ] **Step 2: Add the disable-sync helper**

In the `<script>` IIFE, immediately after the `var pluginId = "...";` line, add a
helper that greys out a Prefer checkbox while its Allow checkbox is unchecked.
Use `.onchange =` (property assignment, not `addEventListener`) so it does not
stack listeners across repeated `pageshow` events:

```javascript
            function syncTextlessPrefer(allowSelector, preferSelector) {
                var allow = document.querySelector(allowSelector);
                var prefer = document.querySelector(preferSelector);
                allow.onchange = function () { prefer.disabled = !allow.checked; };
                prefer.disabled = !allow.checked;
            }
```

- [ ] **Step 3: Load the new fields on pageshow**

In the `pageshow` handler, immediately after the
`#IncludeNoLanguageForLogos` load line, add:

```javascript
                        document.querySelector('#PreferNoLanguageForPosters').checked = !!config.PreferNoLanguageForPosters;
                        document.querySelector('#PreferNoLanguageForBackdrops').checked = !!config.PreferNoLanguageForBackdrops;
                        document.querySelector('#PreferNoLanguageForLogos').checked = !!config.PreferNoLanguageForLogos;
                        syncTextlessPrefer('#IncludeNoLanguageForPosters', '#PreferNoLanguageForPosters');
                        syncTextlessPrefer('#IncludeNoLanguageForBackdrops', '#PreferNoLanguageForBackdrops');
                        syncTextlessPrefer('#IncludeNoLanguageForLogos', '#PreferNoLanguageForLogos');
```

- [ ] **Step 4: Save the new fields on submit**

In the `submit` handler, immediately after the
`config.IncludeNoLanguageForLogos = ...` save line, add:

```javascript
                        config.PreferNoLanguageForPosters = document.querySelector('#PreferNoLanguageForPosters').checked;
                        config.PreferNoLanguageForBackdrops = document.querySelector('#PreferNoLanguageForBackdrops').checked;
                        config.PreferNoLanguageForLogos = document.querySelector('#PreferNoLanguageForLogos').checked;
```

- [ ] **Step 5: Build (verifies the resource still embeds)**

Run: `dotnet build`
Expected: `Build succeeded`, 0 errors. `configPage.html` is an `EmbeddedResource`,
so a successful build confirms the file is still well-formed enough to embed.
(Visual/interaction correctness is checked in the final manual verification.)

- [ ] **Step 6: Commit**

```bash
git add Configuration/configPage.html
git commit -m "feat: add prefer-textless checkboxes to config page (#3)"
```

---

### Task 3: Documentation (README + CLAUDE.md)

**Files:**
- Modify: `README.md:42` (config table), `README.md:52-57` (bucket-order paragraph), and add a new subsection after line 57
- Modify: `CLAUDE.md` (the bucket-ranking diagram, the `rank 3: textless` line)

**Interfaces:** none (docs only).

- [ ] **Step 1: Add the three config-table rows**

In `README.md`, immediately after the `IncludeNoLanguageForLogos` table row
(line 42), add:

```markdown
| `PreferNoLanguageForPosters`     | `false` | Rank textless posters above titled art. Needs `IncludeNoLanguageForPosters`. |
| `PreferNoLanguageForBackdrops`   | `false` | Rank textless backdrops first (clean, no burned-in title). Needs `IncludeNoLanguageForBackdrops`. |
| `PreferNoLanguageForLogos`       | `false` | Rank textless logos first. Needs `IncludeNoLanguageForLogos`.                |
```

- [ ] **Step 2: Update the bucket-order paragraph**

In `README.md`, replace the sentence at lines 52-57. Old text:

```markdown
The bucket order is preferred, fallback, then original (opt-in, dead-last by
default, see `OriginalLanguageLast`), then textless (opt-in per type), with a
`vote_count DESC` tiebreak within each bucket. The exact order is enforced
through Jellyfin's own downstream image sort, so the picker shows synthetic
ratings rather than TMDB's and labels every image with the library language
even when it is really a regional variant or the original language.
```

New text:

```markdown
The bucket order is preferred, fallback, then original (opt-in, dead-last by
default, see `OriginalLanguageLast`), then textless (opt-in per type, or moved
to the very top instead when `PreferNoLanguageFor*` is on for that type), with a
`vote_count DESC` tiebreak within each bucket. The exact order is enforced
through Jellyfin's own downstream image sort, so the picker shows synthetic
ratings rather than TMDB's and labels every image with the library language
even when it is really a regional variant or the original language (see "Note on
the image picker" below).
```

- [ ] **Step 3: Add the "Note on the image picker" subsection**

In `README.md`, immediately after the paragraph edited in Step 2 (before the
`## Why` heading), add:

```markdown
### Note on the image picker

To make its ordering survive Jellyfin's own downstream image sort and filter,
this plugin tags every image it returns with the library's metadata language and
encodes the bucket rank as a synthetic community rating. Two visible
consequences, both by design and not bugs:

- In the *Edit Images* dialog, images are labelled with the library language
  (and show synthetic ratings) even when the artwork is really English,
  textless, or another language. Changing the library's preferred download
  language changes those labels accordingly.
- The dialog's **"All languages"** toggle has no visible effect, because every
  image already carries the library-language tag its filter keys on.

This is the deliberate trade-off that guarantees the exact bucket order you
configured. If you would rather see real per-image language tags and give up the
ordering guarantee, use Jellyfin's built-in TMDB image provider instead.
```

- [ ] **Step 4: Update the CLAUDE.md bucket diagram**

In `CLAUDE.md`, in the "Bucket ranking" code block, replace the line:

```
rank 3: textless (null)     (opt-in per image type)
```

with:

```
rank 3: textless (null)     (opt-in per image type; moved to rank -1, above the
                             preferred bucket, when PreferNoLanguageFor{Posters,
                             Backdrops,Logos} is set for that type)
```

- [ ] **Step 5: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs: document prefer-textless option and image-picker trade-off (#3)"
```

---

### Task 4: Version bump to 0.9.0

**Files:**
- Modify: `Jellyfin.Plugin.LanguageAwareImages.csproj:8-9`
- Modify: `meta.json:10-11`

**Interfaces:** none.

- [ ] **Step 1: Bump the assembly version**

In `Jellyfin.Plugin.LanguageAwareImages.csproj`, change lines 8-9:

```xml
    <AssemblyVersion>0.9.0.0</AssemblyVersion>
    <FileVersion>0.9.0.0</FileVersion>
```

- [ ] **Step 2: Bump meta.json version and changelog**

In `meta.json`, set `version` (line 10) to `"0.9.0.0"` and replace the
`changelog` (line 11) with:

```json
  "changelog": "Adds per-image-type prefer-textless options (PreferNoLanguageForPosters/Backdrops/Logos, default off). When textless is allowed for a type you can now rank the no-language version above titled art instead of dead-last, which is what most people want for backdrops and logos. Also documents the image-picker trade-off (issue #3): images are tagged with the library metadata language and the Edit Images 'All languages' toggle has no effect, both by design so the exact bucket order survives Jellyfin's downstream sort.",
```

- [ ] **Step 3: Build to confirm the version compiles**

Run: `dotnet build`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Jellyfin.Plugin.LanguageAwareImages.csproj meta.json
git commit -m "chore: bump to 0.9.0 for prefer-textless feature"
```

---

## Final verification (manual, once, after all tasks)

Per the spec's testing section. Requires a running Jellyfin and a title that has
both textless and English backdrops on TMDB.

- [ ] Run `./build.sh` (publishes and installs both DLLs into the local Jellyfin
      plugin dir, restarts Jellyfin). Enable Debug logging in Jellyfin.
- [ ] On a library whose preferred download language is not English (e.g.
      Polish) with `FallbackLanguage=en` and "Allow textless backdrops" on:
      enable "Prefer textless backdrops over titled", save, refresh metadata on
      the title. Confirm the textless backdrop is now first in the picker. In
      the Jellyfin log (`LanguageAwareImages` prefix) confirm the textless
      backdrop URL sits at the top of the ranking.
- [ ] Toggle "Prefer textless backdrops" off, save, refresh. Confirm titled
      English returns to the top (regression check).
- [ ] Confirm the "Prefer" checkbox greys out when its "Allow" checkbox is
      unchecked in the config page.
- [ ] Confirm a config upgraded from 0.8.1 (new bools untouched) behaves exactly
      as before: textless backdrops last, all three Prefer bools default false.

## Handoff to the issue

- [ ] After merge/release, reply on issue #3: symptom 1 is fixed via the new
      per-type prefer-textless options; symptoms 2 and 3 (language tags, "All
      languages" toggle) are the documented ordering trade-off, now called out in
      the README's "Note on the image picker".
