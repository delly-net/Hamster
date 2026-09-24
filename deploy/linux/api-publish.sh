#/bin/bash
name="hamster-api"
server2="docker.jueyun.net"
version=$(date +%Y%m%d%H%M%S)
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
echo "done"