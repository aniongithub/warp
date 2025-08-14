#!/bin/bash
set -e
mkdir -p /var/run

# Default values
RUN_WARP=${RUN_WARP:-true}
RUN_PLASMA=${RUN_PLASMA:-false}

echo "Starting supervisord..."
# Start supervisord
/usr/bin/supervisord -c /etc/supervisord.conf &

# Wait until the socket is ready
for i in $(seq 1 30); do
  supervisorctl status >/dev/null 2>&1 && break
  sleep 0.2
done

# Conditionally start warp components
if [ "$RUN_WARP" = "true" ]; then
  echo "Starting warp components..."
  supervisorctl start warp-gateway
  supervisorctl start dev-api
  supervisorctl start admin-api
else
  echo "Warp components disabled"
fi

# Conditionally start plasma
if [ "$RUN_PLASMA" = "true" ]; then
  echo "Starting warp plasma..."
  supervisorctl start warp-plasma
else
  echo "Warp plasma disabled"
fi

# Check if any services were started
if [ "$RUN_WARP" != "true" ] && [ "$RUN_PLASMA" != "true" ]; then
  echo "ERROR: At least one of RUN_WARP or RUN_PLASMA must be true"
  exit 1
fi

echo "Services started successfully"

# Keep container attached to supervisord
wait "$(cat /var/run/supervisord.pid)"
