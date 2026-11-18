# Release and recovery runbook

Publishing a **stable** GitHub Release builds the image, publishes it to GHCR and deploys it to the
`production` environment (Azure App Service). Drafts and prereleases never deploy.

Flow: `release: released` → guard (tag format, tag commit is on `main`) → CI on the exact commit →
publish `ghcr.io/live360gaming/turingtown:vX.Y.Z` → verify the tag resolves to the pushed digest →
deploy that **digest** → confirm Azure holds the digest → require HTTP 200 on `/` and `/api/test`.
The Deployments tab records the result and links to the live URL.

## Cut a release

1. Pick the commit on `main` and confirm CI is green for that exact SHA:
   `gh run list --workflow CI --branch main --json headSha,conclusion,displayTitle`
2. Pick the next unused patch: `gh release list` and `git tag -l`. Never reuse or move a tag.
3. Publish (a person's credentials, so the workflow triggers):
   `gh release create vX.Y.Z --target <full-40-char-SHA> --generate-notes --title vX.Y.Z`
   - Do not pass `--draft` or `--prerelease` unless you do **not** want a production deploy.
     Publishing the draft later, or promoting the prerelease to stable, deploys then.
   - If release creation is ever automated, use a GitHub App token or PAT. Releases created with the
     default `GITHUB_TOKEN` do not trigger workflows, so nothing would deploy.

## Verify

- Releases tab shows vX.Y.Z with the expected notes. Exactly one **Deploy** run starts for it.
- The run summary lists the commit and the deployed digest.
- The tag resolves to that digest: `scripts/ghcr-resolve.sh --require-amd64 live360gaming/turingtown vX.Y.Z`
  (or `docker buildx imagetools inspect ghcr.io/live360gaming/turingtown:vX.Y.Z`).
- Deployments → production shows a **success** for the same commit, linking to the live site.
  `gh api repos/Live360Gaming/TuringTown/deployments?environment=production --jq '.[0]|{sha,id}'`
  then `gh api repos/Live360Gaming/TuringTown/deployments/<id>/statuses --jq '.[0].state'`.
- `curl -s -o /dev/null -w '%{http_code}\n' <url>/` and `<url>/api/test` both print `200`.

## Recovery

- **Deploy failed after the image published**: open the failed run. The log names the failing path
  and status, and the "Failure diagnostics" step prints App Service state, the container's image and
  port, and filtered platform log lines (credentials are never printed). Fix the cause, then use
  **Re-run failed jobs**. That redeploys the same already-published digest. "Re-run all jobs" fails
  at publish by design, because version tags are immutable.
- **CI failed on the release commit** (nothing was published): fix on `main`, then cut the next
  patch. Do not delete or move the failed tag or release.
- **Roll back**: re-running an old successful run is blocked (its tag already exists). Prefer
  rolling forward with a new patch release. If production must go back immediately, an operator sets
  the `main` site container's image to the last good release's digest (listed in that run's summary)
  in the Portal or with the `az rest` PUT shown in `deploy.yml`, then restarts the app.
  This is a manual production change and needs the owner's approval.
- **Never** retag or delete GHCR versions, and never edit historical deployment statuses. The two
  untagged GHCR entries under each release are the linux/amd64 image manifest and its build
  attestation; the release tag points at the index that references both.

## If Azure access is unavailable: 503 checklist (cause not confirmed)

The v0.0.1 deploy ended with repeated HTTP 503 after earlier 404s and timeouts; the cause was never
captured. An operator with Portal access should record, not guess:

1. **Deployment Center / Log stream**: any image pull failure (GHCR auth, manifest not found,
   platform mismatch)? Does the platform log show the container starting and then
   "didn't respond to HTTP pings on port"?
2. **Site container `main`**: `image` is the expected `@sha256:` digest, `isMain` is true, and
   `targetPort` is 8080. A pinned registry credential, if the package is private, is present.
3. **App settings**: `WEBSITES_PORT` (if set) is 8080 and not conflicting with `targetPort`;
   `WEBSITES_CONTAINER_START_TIME_LIMIT` is large enough for a cold pull.
4. **Container log**: Kestrel prints `Now listening on: http://[::]:8080`. The image exposes 8080 and
   runs as a non-root user (see `Dockerfile`).
5. **Plan and app state**: the app is Running, not stopped, quota-limited or scaled to a plan that
   cannot start the container. Check any configured Health check path returns 200.
6. Record what you found in the PR or incident issue.
