#!/bin/bash
echo "[==========拉取代码==========]"
cd /project/delly-net/Hamster
git checkout main
git pull
echo "[==========编译代码==========]"
mkdir -p /project/delly-net/publish/hamster-api/files
cd /project/delly-net/Hamster/api
/usr/bin/dotnet build Hamster.Api.csproj -c Release -r linux-musl-x64 -p:IsPackable=false -o /project/delly-net/publish/hamster-api/files
echo "[==========编译完成==========]"