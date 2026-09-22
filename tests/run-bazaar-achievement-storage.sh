#!/usr/bin/env bash
set -euo pipefail
scratch=${COFLNET_TASK_SCRATCH:-/task/scratch}
mkdir -p "$scratch"
log=$(mktemp "$scratch/bazaar-storage.XXXXXX.log")
exec 3>&1
cleanup() {
  status=$?
  docker rm -fv bazaar-achievement-client bazaar-achievement-cassandra bazaar-achievement-redis >>"$log" 2>&1 || true
  if (( status != 0 )); then tail -35 "$log" >&3; else rm -f "$log"; fi
  exit "$status"
}
trap cleanup EXIT
{
 testenv create
 docker rm -fv bazaar-achievement-client bazaar-achievement-cassandra bazaar-achievement-redis >/dev/null 2>&1 || true
 docker build -t bazaar-achievement-sdk:fixture -f - . <<'DOCKER'
FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /build
RUN git init dev && cd dev && git remote add origin https://github.com/Coflnet/HypixelSkyblock.git && git fetch --depth=1 origin 763cbebc3d4c2301e643086ec64dfcff4aabcf33 && git checkout FETCH_HEAD
WORKDIR /build/sky
COPY . .
RUN dotnet restore && dotnet build --no-restore
DOCKER
 docker run -d --network kind --name bazaar-achievement-cassandra --memory 2g --cpus 2 -e MAX_HEAP_SIZE=512M -e HEAP_NEWSIZE=100M cassandra:4.1.8
 docker run -d --network kind --name bazaar-achievement-redis --memory 128m --cpus 0.25 redis:7.4.2-alpine
 ready=false
 for attempt in $(seq 1 60); do
   if docker exec bazaar-achievement-cassandra cqlsh -e 'SELECT release_version FROM system.local;' >/dev/null 2>&1; then ready=true; break; fi
   sleep 4
 done
 "$ready"
 timeout 240 docker run --rm --name bazaar-achievement-client --network kind --memory 2g --cpus 2 -e BAZAAR_STORAGE_TEST=bazaar-achievement-cassandra -e BAZAAR_REDIS_TEST=bazaar-achievement-redis bazaar-achievement-sdk:fixture dotnet test --no-build --filter FullyQualifiedName~BazaarAchievementStorageTests --logger 'console;verbosity=normal' > "$scratch/bazaar-storage-result.log" 2>&1 || { cat "$scratch/bazaar-storage-result.log"; exit 1; }
 grep -Eq 'Passed: +[1-9]|Tests passed: [1-9]' "$scratch/bazaar-storage-result.log"
} >>"$log" 2>&1
printf 'Bazaar storage regression passed; executed tests:\n' >&3
grep -E 'Passed |Total tests:|Passed:' "$scratch/bazaar-storage-result.log" >&3
rm -f "$scratch/bazaar-storage-result.log"
