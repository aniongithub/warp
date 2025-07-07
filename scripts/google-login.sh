#!/bin/bash

# Fail on any error
set -euo pipefail

# Start the login process
if gcloud auth list --format="value(account)" | grep -q .; then
  echo "✅ Already logged in as: $(gcloud auth list --format='value(account)')"
else
  echo "🔐 Not logged in. Run:"
  echo "gcloud auth login --no-launch-browser"
fi

if [ $? -ne 0 ]; then
  echo "Failed to authenticate with gcloud"
  exit 1
fi