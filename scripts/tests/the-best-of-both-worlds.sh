#!/bin/bash

HOST=${WARP_HOST:-"http://localhost:5000"}

# Get the script directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

curl -X GET "${HOST}/api/basic/v1/rest/episode/search?title=The+Best+Of+Both+Worlds" \
     -H "x-api-key: $(${SCRIPT_DIR}/get-api-key.sh)"