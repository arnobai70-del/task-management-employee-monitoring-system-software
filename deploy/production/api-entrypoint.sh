#!/bin/sh
set -eu

source_dir=/run/secrets
target_dir=/run/taskmonitoring-secrets
operations_dir=/var/lib/taskmonitoring/operations

if [ ! -d "$source_dir" ]; then
  echo "Required Docker secret directory is missing: $source_dir" >&2
  exit 1
fi

install -d -o app -g app -m 0700 "$target_dir"
install -d -o app -g app -m 0750 "$operations_dir"
for key in ConnectionStrings__DefaultConnection Jwt__SigningKey; do
  source_path="$source_dir/$key"
  target_path="$target_dir/$key"
  if [ ! -s "$source_path" ]; then
    echo "Required Docker secret is missing or empty: $source_path" >&2
    exit 1
  fi
  install -o app -g app -m 0400 "$source_path" "$target_path"
done

export TASKMONITORING_KEY_PER_FILE_DIRECTORY="$target_dir"
exec gosu app dotnet TaskMonitoring.Api.dll "$@"
