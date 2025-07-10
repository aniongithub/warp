#!/bin/bash

# This script retrieves an API key from our Warp Developer API.

HOST=${WARP_HOST:-"http://localhost:5000"}

# Get the script directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

${SCRIPT_DIR}/../google-login.sh --bare

API_KEY=$(curl -s -f -H "Authorization: Bearer $(gcloud auth print-identity-token)" ${HOST}/developer/api-keys | jq -r '.[0].key')

if [[ -z "$API_KEY" ]]; then
  echo "❌ Failed to fetch API key."
  exit 1
fi

echo "$API_KEY"