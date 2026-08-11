#!/bin/sh
set -e

# NOTE: Render's free plan has no persistent disk. The SQLite DB and any
# uploaded files live in the container's ephemeral filesystem and will be
# reset on every deploy and whenever the service spins down/up after idling.
# DbSeeder re-seeds demo data automatically on each fresh start.

export ASPNETCORE_URLS="http://+:${PORT:-10000}"

# Disable config file-watching (FileSystemWatcher). Render's containers have
# a low inotify instance limit, and hot-reload of appsettings.json isn't
# needed in production anyway - this avoids the IOException crash on boot.
export DOTNET_hostBuilder__reloadConfigOnChange="false"

exec dotnet IForm.Web.dll