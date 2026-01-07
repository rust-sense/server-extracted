#!/usr/bin/env bash

gh act \
  --platform ubuntu-latest=catthehacker/ubuntu:act-latest \
  --artifact-server-path artifacts \
  --cache-server-path cache \
  --secret-file .secrets \
  --input create_release=false \
  "$@" | tee run-act.log

# Unzip all generated artifacts
if [ -d "artifacts" ]; then
  find artifacts -type f -name "*.zip" | while read -r ARTIFACT; do
    ARTIFACT_DIR=$(dirname "$ARTIFACT")
    ARTIFACT_NAME=$(basename "$ARTIFACT" .zip)
    echo "Unzipping artifact: $ARTIFACT"
    unzip -o "$ARTIFACT" -d "$ARTIFACT_DIR/$ARTIFACT_NAME"
    echo "Artifact extracted to: $ARTIFACT_DIR/$ARTIFACT_NAME"
  done
  
  # Show extracted proto files
  echo ""
  echo "=== Extracted Proto Files ==="
  find artifacts -name "*.proto" -exec echo "Found: {}" \; -exec head -30 {} \;
fi
