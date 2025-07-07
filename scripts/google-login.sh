#!/bin/bash

# Fail on any error
set -euo pipefail

# Check if --bare flag is provided
BARE_MODE=false
for arg in "$@"; do
  if [ "$arg" == "--bare" ]; then
    BARE_MODE=true
    break
  fi
done

# Start the login process
if gcloud auth list --format="value(account)" | grep -q .; then
  if [ "$BARE_MODE" = false ]; then
    echo "✅ Already logged in as: $(gcloud auth list --format='value(account)')"
  fi
else
  if [ "$BARE_MODE" = false ]; then
    echo "🔐 Not logged in. Please log in to your Google account."
  fi
  gcloud auth login --no-launch-browser
fi

if [ $? -ne 0 ]; then
  echo "Failed to authenticate with gcloud"
  exit 1
fi