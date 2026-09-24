#!/bin/bash
mkdir -p /project/delly-net/publish/hamster-api
cp ./api/* /project/delly-net/publish/hamster-api/

mkdir -p /project/delly-net/publish/hamster-ui
cp ./ui/* /project/delly-net/publish/hamster-ui/

mkdir -p /project/delly-net
cd /project/delly-net
git clone git@github.com:delly-net/Hamster.git
cd /project/delly-net/Hamster
git checkout --track origin/main