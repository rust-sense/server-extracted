#!/usr/bin/env bash

gh act \
  --platform ubuntu-latest=catthehacker/ubuntu:act-latest \
  --artifact-server-path artifacts \
  --cache-server-path cache \
  --secret-file .secrets \
  --input create_release=false \
  "$@" | tee run-act.log

