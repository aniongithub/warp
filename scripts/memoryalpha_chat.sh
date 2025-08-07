#!/bin/bash

# Get the script directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Obtain an API key
API_KEY=$(${SCRIPT_DIR}/get-api-key.sh)

# Interactive chat script for MemoryAlpha RAG API
QUOTA="basic"
BASE_URL="http://localhost:5000/api/${QUOTA}"
THINKING_MODE="DISABLED"
MAX_TOKENS=512
TOP_K=5
TOP_P=0.8
TEMPERATURE=0.3

echo "🖖 Welcome to MemoryAlpha RAG Chat"
echo "Type 'quit' or 'exit' to end the session"
echo "----------------------------------------"

while true; do
    # Prompt for user input
    echo -n "❓ Ask about Star Trek: "
    read -r question
    
    # Check for exit commands
    if [[ "$question" == "quit" || "$question" == "exit" || "$question" == "q" ]]; then
        echo "🖖 Live long and prosper!"
        break
    fi
    
    # Skip empty questions
    if [[ -z "$question" ]]; then
        continue
    fi
    
    # URL encode the question
    encoded_question=$(printf '%s' "$question" | jq -sRr @uri)
    
    echo "🤖 LCARS Response:"
    echo "----------------------------------------"
    # Make the streaming request and capture status code and error
    response=$(curl -s -N -w "%{http_code}" \
        -H "x-api-key: ${API_KEY}" \
        -H "Accept: text/event-stream" \
        "${BASE_URL}/memoryalpha/rag/stream?question=${encoded_question}&thinkingmode=${THINKING_MODE}&max_tokens=${MAX_TOKENS}&top_k=${TOP_K}&top_p=${TOP_P}&temperature=${TEMPERATURE}")
    http_code="${response: -3}"
    body="${response:0:${#response}-3}"

    if [[ "$http_code" -ne 200 ]]; then
        echo "❌ Error: HTTP $http_code"
        echo "$body"
    else
        echo "$body" | while IFS= read -r line; do
            if [[ $line == data:* ]]; then
                chunk=$(echo "${line#data: }" | jq -r '.chunk // empty')
                if [[ -n "$chunk" ]]; then
                    printf "%s" "$chunk"
                fi
            fi
        done
    fi
    echo -e "\n----------------------------------------"
done