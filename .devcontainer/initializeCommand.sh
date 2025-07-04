#!/bin/bash

if [ ! -f /workspace/.env ]; then
    echo "You will need to create a .env file in the root of the project."
else
  echo ".env file already exists."
fi

# Create a data directory if it doesn't exist
mkdir -p /workspace/data