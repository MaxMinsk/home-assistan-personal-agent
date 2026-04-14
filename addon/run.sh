#!/usr/bin/with-contenv bashio
set -euo pipefail

mkdir -p /data/workspace

bashio::log.info "Starting Home Assistant Personal Agent"
exec /opt/ha-personal-agent/HaPersonalAgent
