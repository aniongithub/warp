#!/bin/bash

# This script retrieves an API key from our Warp Developer API.

API_KEY=$(curl -s -f -H "Authorization: Bearer $(gcloud auth print-identity-token)" http://localhost:5000/developer/api-keys | jq -r '.[0].key')

if [[ -z "$API_KEY" ]]; then
  echo "❌ Failed to fetch API key."
  exit 1
fi

echo "$API_KEY"