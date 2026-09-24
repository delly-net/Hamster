#!/bin/bash
echo "[==========拉取代码==========]"
cd /project/delly-net/Hamster
git checkout main
git pull
cd /project/delly-net/Hamster/ui
echo "[==========更新依赖==========]"
npm install
echo "[==========编译代码==========]"
npm run build
echo "[==========复制文件==========]"
mkdir -p /project/delly-net/publish/hamster-ui/files
rm -rf /project/delly-net/publish/hamster-ui/files/*
cp -r ./dist/* /project/delly-net/publish/hamster-ui/files/
echo "[==========编译完成==========]"