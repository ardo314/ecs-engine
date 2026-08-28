#!/bin/sh
# Applies deploy-time BASE_PATH / EDITOR_BACKEND_URL to the prebuilt site.
set -eu

base_path=$(printf '/%s/' "${BASE_PATH:-}" | sed 's|//*|/|g')
backend_url=$(printf '%s' "${EDITOR_BACKEND_URL:-http://localhost:5000}" | sed 's|/*$||')
backend_js=$(printf '%s' "$backend_url" | sed 's|\\|\\\\|g; s|"|\\"|g')

root=/usr/share/nginx/html
rm -rf "$root"
mkdir -p "${root}${base_path}"
cp -a /opt/site/. "${root}${base_path}"

printf 'window.__EDITOR_CONFIG__ = { backendUrl: "%s" };\n' "$backend_js" \
  > "${root}${base_path}config.js"

sed "s|__BASE_PATH__|${base_path}|g" /opt/nginx.conf.template > /etc/nginx/conf.d/default.conf
