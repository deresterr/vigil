# Releasing

A version's section in [`CHANGELOG.md`](../CHANGELOG.md) is also its release notes: the release workflow takes the
section, adds a footer (the server image, the client file, a compare link) and puts it on the GitHub release. So
the section is written once, for the parent who runs Vigil, and in the same shape every time.

## The template

````markdown
## [1.2.0] - 2026-12-01

One or two sentences: what this release brings, in the parent's words. Not a list of the changes.

| | Update |
|---|---|
| Server | **Yes.** New image; the database is updated at start: back up `data/` before. |
| Children's PCs | **Yes.** Run the new `vigil_setup.exe` → **Update**. |

**Changed:** only for a change that needs the parent's attention (something works differently, or they have to
do something). Leave it out otherwise.

### Panel
- One item per change, one or two sentences: what is different now, and what it was before if that helps.

### Lock screen and tray
- …
````

**Summary.** The first paragraph, right under the heading. It says what the release is about, not how many
changes it has: "Small fixes in the parent panel: …", "Fixes "Shut down" on the lock screen, …".

**Update table.** Always both rows, in this order, each starting with one of these answers in bold:

| Row | Answers |
|---|---|
| `Server` | **Yes.** / **Yes, first.** (the PCs need the new server) / **Not needed.** |
| `Children's PCs` | **Yes.** / **Optional.** / **Not needed.** |

After the answer, one short sentence: whether the database changes (and to back it up), whether the PCs need a
new pairing, whether an older client keeps working with the new server.

**Groups.** Only the ones with changes, in this order: `Panel`, `Lock screen and tray`, `Installer`, `Server`,
`Security and maintenance`, `Documentation`. A release with one striking change can open with `### Highlights`
(two or three items) before the groups; each highlight still appears in its group.

**Items.**

- Name what the parent sees, in the words of the interface ("Settings → Add computer", "the Journal"), not the code.
- Say what is different now. A fix says what went wrong before: "… could stop the server for 30 seconds".
- One or two sentences, no issue numbers, no file names unless the parent types them (`compose.yaml`, `data/`).
- Nothing from a real environment: no addresses, host names or names of children.

A withdrawn version keeps its section with a quote under the heading:
`> **Withdrawn:** <what was wrong> (fixed in X.Y.Z). Its release and server image were removed.`

Sections before 0.8.2 predate the template and stay as they are.

## Pull requests

Every pull request with a user-visible change adds its items under `## [Unreleased]`, in the group they belong
to. The summary and the update table are written at the release.

## Making a release

1. **Release pull request.** Rename `## [Unreleased]` to `## [X.Y.Z] - <date>` and add a new empty
   `## [Unreleased]` above it. Write the summary and the update table, tidy the items, and update the links at the
   end of the file. In both READMEs, update the version in the `compose.yaml` comment (`e.g. :X.Y.Z`). Check the
   section: `python3 .github/scripts/release_notes.py X.Y.Z` prints the notes, or says what does not follow the
   template.
2. **Merge it.**
3. **Publish.** Releases → **Draft a new release** → **Choose a tag** → type `vX.Y.Z` → **Create new tag:
   vX.Y.Z on publish** (target `main`). Leave the title and the description empty and click **Publish release**.
4. **Wait for the `release` workflow** (about 10 minutes). It checks the section first and publishes nothing if
   it is missing or out of shape; then it pushes the server image, attaches `vigil_setup.exe`, and sets the title
   (`Vigil vX.Y.Z`) and the notes.

If the workflow stops at the notes, the tag points at a commit without a correct section: delete the release and
the tag (the release page → **Delete**; Tags → the tag → **Delete**), fix `CHANGELOG.md` on `main`, and publish
again. A notes-only change after a successful release: edit the release on GitHub, and fix the section in
`CHANGELOG.md` the same way, so the two do not drift apart.

If the image push ends with `denied`: the package's settings → **Manage Actions access** → this repository with
the **Write** role, then **Re-run failed jobs**.
