#!/bin/bash
echo "[==========拉取代码==========]"
cd /project/delly-net/Hamster
git checkout main
git pull

echo "[==========编译后端代码==========]"
mkdir -p /project/delly-net/publish/hamster-api/files
cd /project/delly-net/Hamster/api
/usr/bin/dotnet build Hamster.Api.csproj -c Release -r linux-musl-x64 -p:IsPackable=false -o /project/delly-net/publish/hamster-api/files

echo "[==========更新前端依赖==========]"
cd /project/delly-net/Hamster/ui
npm install
echo "[==========编译前端代码==========]"
npm run build
echo "[==========复制前端文件==========]"
mkdir -p /project/delly-net/publish/hamster-ui/files
rm -rf /project/delly-net/publish/hamster-ui/files/*
cp -r ./dist/* /project/delly-net/publish/hamster-ui/files/

version=$(date +%Y%m%d%H%M%S)

echo "[==========发布后端镜像==========]"
name="hamster-api"
server2="docker.jueyun.net"
cd /project/delly-net/publish/hamster-api
echo "[+++] docker build -t $name:$version ."
docker build -t $name:$version .
# 推送到docker.jueyun.net
echo "[>>>] docker tag $name:$version $server2/$name:$version"
docker tag $name:$version $server2/$name:$version
echo "[>>>] docker push $server2/$name:$version"
docker push $server2/$name:$version
# 移除本地镜像
echo "[---] docker rmi $name:$version"
docker rmi $name:$version
echo "[---] docker rmi $server2/$name:$version"
docker rmi $server2/$name:$version

echo "[==========发布前端镜像==========]"
name="hamster-ui"
server2="docker.jueyun.net"
cd /project/delly-net/publish/hamster-ui
echo "[+++] docker build -t $name:$version ."
docker build -t $name:$version .
# 推送到docker.jueyun.net
echo "[>>>] docker tag $name:$version $server2/$name:$version"
docker tag $name:$version $server2/$name:$version
echo "[>>>] docker push $server2/$name:$version"
docker push $server2/$name:$version
# 移除本地镜像
echo "[---] docker rmi $name:$version"
docker rmi $name:$version
echo "[---] docker rmi $server2/$name:$version"
docker rmi $server2/$name:$version

echo "[==========操作完成==========]"