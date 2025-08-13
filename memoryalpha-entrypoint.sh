#!/bin/bash
set -e
mkdir -p /var/run

# start supervisord
/usr/bin/supervisord -c /etc/supervisord.conf &
# wait until the socket is ready
for i in $(seq 1 30); do
  supervisorctl status >/dev/null 2>&1 && break
  sleep 0.2
done

# conditionally start plasma
if [ "$RUN_WARP_PLASMA" = "true" ]; then
  echo "Starting warp_plasma..."
  supervisorctl start warp_plasma
else
  echo "warp_plasma disabled"
fi

# keep container attached to supervisord
wait "$(cat /var/run/supervisord.pid)"
