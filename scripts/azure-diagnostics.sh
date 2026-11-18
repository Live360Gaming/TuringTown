#!/usr/bin/env bash
# Best-effort App Service diagnostics for a failed deploy. Requires an authenticated `az` session.
# Usage: azure-diagnostics.sh APP_NAME RESOURCE_GROUP
#
# Prints only non-secret facts: app state, the container image/port config, app-setting NAMES
# (values only for an allowlist of non-secret keys), and filtered container log lines.
# Registry credentials (userName / passwordSecret) and all other app-setting values are never printed.
# Every section is independent and non-fatal; a section that cannot run says so and the operator
# checklist in docs/RELEASING.md applies.
set -u

app="$1"
rg="$2"

section() { echo; echo "::group::$1"; }
endsection() { echo "::endgroup::"; }
unavailable() { echo "UNAVAILABLE: $1 (not accessible with this identity; see docs/RELEASING.md operator checklist)"; }

section "App state"
az webapp show --name "$app" --resource-group "$rg" \
  --query "{state:state, availabilityState:availabilityState, host:defaultHostName, kind:kind, httpsOnly:httpsOnly}" \
  -o json 2>&1 || unavailable "az webapp show"
endsection

section "Site config (health check, always-on)"
az webapp config show --name "$app" --resource-group "$rg" \
  --query "{linuxFxVersion:linuxFxVersion, healthCheckPath:healthCheckPath, alwaysOn:alwaysOn, appCommandLine:appCommandLine}" \
  -o json 2>&1 || unavailable "az webapp config show"
endsection

section "Site containers (credentials omitted)"
sub=$(az account show --query id -o tsv 2>/dev/null)
az rest --method get \
  --url "https://management.azure.com/subscriptions/$sub/resourceGroups/$rg/providers/Microsoft.Web/sites/$app/sitecontainers?api-version=2024-04-01" \
  --query "value[].{name:name, image:properties.image, targetPort:properties.targetPort, isMain:properties.isMain, authType:properties.authType, startUpCommand:properties.startUpCommand}" \
  -o json 2>&1 || unavailable "list sitecontainers"
endsection

section "App setting names, and port/startup values only"
az webapp config appsettings list --name "$app" --resource-group "$rg" \
  --query "[?name=='WEBSITES_PORT' || name=='PORT' || name=='WEBSITES_CONTAINER_START_TIME_LIMIT' || name=='WEBSITES_ENABLE_APP_SERVICE_STORAGE' || name=='ASPNETCORE_URLS' || name=='ASPNETCORE_HTTP_PORTS'].{name:name, value:value}" \
  -o json 2>&1 || unavailable "list app settings (allowlisted values)"
echo "All setting names:"
az webapp config appsettings list --name "$app" --resource-group "$rg" --query "[].name" -o tsv 2>&1 \
  || unavailable "list app setting names"
endsection

section "Container platform log (image pull, startup, port) - filtered tail"
tmp=$(mktemp -d)
if az webapp log download --name "$app" --resource-group "$rg" --log-file "$tmp/logs.zip" >/dev/null 2>"$tmp/err.txt" \
   && unzip -q -o "$tmp/logs.zip" -d "$tmp/logs" 2>/dev/null; then
  for f in "$tmp"/logs/LogFiles/*docker.log "$tmp"/logs/LogFiles/*default_docker.log; do
    [ -f "$f" ] || continue
    echo "--- $(basename "$f") (last matching lines)"
    grep -iE "pull|manifest|unauthorized|denied|not found|start|listening|port|didn't respond|timed out|exit|error|fail" "$f" \
      | tail -n 60 | cut -c1-400
  done
else
  unavailable "az webapp log download ($(tr '\n' ' ' <"$tmp/err.txt" | cut -c1-200))"
fi
rm -rf "$tmp"
endsection

exit 0
