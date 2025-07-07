#!/bin/bash

# Get the script directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

curl -X GET "http://localhost:5000/api/basic/v1/rest/episode/search?title=The+Best+Of+Both+Worlds" \
     -H "x-api-key: $(${SCRIPT_DIR}/get-api-key.sh)"