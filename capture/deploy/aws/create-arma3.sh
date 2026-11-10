#!/usr/bin/env bash
# Creates arma3-server on an EC2 machine (Ubuntu 24.04) and runs the Arma 3 dedicated server (BattlEye off, document/games/arma3.md §5);
# run it again to update the server config, difficulty profile and test mission to this repository's HEAD (uncommitted changes are not sent):
#   [ARMA3_INSTANCE_TYPE=m6a.large] bash create-arma3.sh [server build folder]
# Without a server build folder, the machine installs the current stable server itself with SteamCMD (app 233780, anonymous
# login) when it has none yet: the players' Steam clients are on that same version. A server build folder pins a version
# instead: the Linux build of app 233780 (content depot 233781 + Linux binary depot 233783 merged, document/games/arma3.md
# "Pinned server builds"), downloaded on a PC with DepotDownloader and sent with rsync (only changed files on later runs).
# m6a.large (2 vCPU, 8 GB, ~0.10 $/h in Singapore) carries ~20 players: the server is one main thread plus a little.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(git -C "$here" rev-parse --show-toplevel)
if [ -f "$here/.aws-credentials" ]; then
  export AWS_SHARED_CREDENTIALS_FILE=$here/.aws-credentials AWS_PROFILE=${AWS_PROFILE:-os}
fi
export AWS_REGION=${AWS_REGION:-ap-southeast-1}
build=${1:-}
name=arma3-server
key=$HOME/.ssh/$name.pem
my_ip=$(curl -fsS https://checkip.amazonaws.com)
[ -z "$build" ] || [ -f "$build/arma3server_x64" ] || { echo "$build/arma3server_x64 missing."; exit 1; }
type=${ARMA3_INSTANCE_TYPE:-m6a.large}

sg=$(aws ec2 describe-security-groups --filters "Name=group-name,Values=$name" --query 'SecurityGroups[0].GroupId' --output text)
if [ "$sg" = None ]; then
  vpc=$(aws ec2 describe-vpcs --filters Name=is-default,Values=true --query 'Vpcs[0].VpcId' --output text)
  sg=$(aws ec2 create-security-group --group-name $name --vpc-id "$vpc" --query GroupId --output text \
    --description "arma3-server: game UDP 2302-2305; SSH from the admin IP")
  # 2302 game + VON, 2303 Steam query, 2304 Steam port, 2305 reserved; 2306 (BattlEye) stays closed.
  aws ec2 authorize-security-group-ingress --group-id "$sg" \
    --ip-permissions 'IpProtocol=udp,FromPort=2302,ToPort=2305,IpRanges=[{CidrIp=0.0.0.0/0}]' > /dev/null
fi
aws ec2 authorize-security-group-ingress --group-id "$sg" --protocol tcp --port 22 --cidr "$my_ip/32" > /dev/null 2>&1 || true

id=$(aws ec2 describe-instances --output text --query 'Reservations[].Instances[].InstanceId' \
  --filters "Name=tag:Name,Values=$name" Name=instance-state-name,Values=pending,running,stopping,stopped)
if [ -z "$id" ]; then
  if [ ! -f "$key" ]; then
    aws ec2 create-key-pair --key-name $name --key-type ed25519 --query KeyMaterial --output text > "$key"
    chmod 600 "$key"
  fi
  ami=$(aws ssm get-parameter --query Parameter.Value --output text \
    --name /aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/hvm/ebs-gp3/ami-id)
  # The server build is about 20 GB.
  id=$(aws ec2 run-instances --image-id "$ami" --instance-type "$type" --key-name $name --security-group-ids "$sg" \
    --block-device-mappings 'DeviceName=/dev/sda1,Ebs={VolumeSize=60,VolumeType=gp3}' \
    --metadata-options HttpTokens=required \
    --tag-specifications "ResourceType=instance,Tags=[{Key=Name,Value=$name}]" \
    --query 'Instances[0].InstanceId' --output text)
  aws ec2 wait instance-running --instance-ids "$id"
  alloc=$(aws ec2 allocate-address --domain vpc --query AllocationId --output text \
    --tag-specifications "ResourceType=elastic-ip,Tags=[{Key=Name,Value=$name}]")
  aws ec2 associate-address --instance-id "$id" --allocation-id "$alloc" > /dev/null
  echo "Created $id."
else
  aws ec2 start-instances --instance-ids "$id" > /dev/null
  aws ec2 wait instance-running --instance-ids "$id"
fi

ip=$(aws ec2 describe-instances --instance-ids "$id" --query 'Reservations[0].Instances[0].PublicIpAddress' --output text)
ssh_=(ssh -i "$key" -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 "ubuntu@$ip")
echo "Waiting for $ip..."
until "${ssh_[@]}" true 2> /dev/null; do sleep 5; done
"${ssh_[@]}" 'sudo mkdir -p /opt/arma3/server /opt/arma3/odx /opt/arma3/profile && sudo chown -R ubuntu:ubuntu /opt/arma3'

if [ -n "$build" ]; then
  rsync -a --partial --exclude .DepotDownloader -e "ssh -i $key" "$build/" "ubuntu@$ip:/opt/arma3/server/"
  "${ssh_[@]}" 'chmod +x /opt/arma3/server/arma3server_x64'
elif ! "${ssh_[@]}" test -f /opt/arma3/server/arma3server_x64; then
  echo "Installing the current stable server with SteamCMD (about 20 GB)..."
  "${ssh_[@]}" 'bash -s' << 'EOF1'
set -euo pipefail
sudo dpkg --add-architecture i386
sudo apt-get update -qq
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq lib32gcc-s1 lib32stdc++6 libcurl4 curl > /dev/null
mkdir -p ~/steamcmd
curl -fsSL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz | tar -xz -C ~/steamcmd
# SteamCMD exits 0 even when the login or the download failed: the binary's presence is the check.
~/steamcmd/steamcmd.sh +force_install_dir /opt/arma3/server +login anonymous +app_update 233780 validate +quit | tail -5
test -f /opt/arma3/server/arma3server_x64 || { echo "SteamCMD did not install the server (anonymous login or download refused from this address)."; exit 3; }
chmod +x /opt/arma3/server/arma3server_x64
EOF1
fi
# The Linux server matches file names case-sensitively: the mission folder, its .pbo and the template in server.cfg are all
# lowercase. The difficulty profile is <-profiles dir>/<-name>.Arma3Profile.
git -C "$repo" archive --format=tar HEAD capture/deploy/aws/arma3 | "${ssh_[@]}" '
set -euo pipefail
t=$(mktemp -d) && tar -x -C "$t"
a=$t/capture/deploy/aws/arma3
cp "$a/server.cfg" /opt/arma3/odx/
cp "$a/odx.Arma3Profile" /opt/arma3/profile/
mkdir -p /opt/arma3/server/mpmissions
python3 -I "$a/pack_pbo.py" "$a/mission/odx_test.stratis" /opt/arma3/server/mpmissions/odx_test.stratis.pbo
rm -rf "$t"'

"${ssh_[@]}" 'bash -s' << 'EOF2'
set -euo pipefail
dpkg -s libcurl4 > /dev/null 2>&1 || sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq libcurl4 > /dev/null
sudo tee /etc/systemd/system/arma3.service > /dev/null << "UNIT"
[Unit]
Description=Arma 3 dedicated server (BattlEye off, Odx test mission)
After=network-online.target
Wants=network-online.target

[Service]
User=ubuntu
WorkingDirectory=/opt/arma3/server
ExecStart=/opt/arma3/server/arma3server_x64 -port=2302 -config=/opt/arma3/odx/server.cfg -profiles=/opt/arma3/profile -name=odx -world=empty -autoInit -limitFPS=60
Restart=on-failure
RestartSec=10
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
UNIT
sudo systemctl daemon-reload
sudo systemctl enable arma3 > /dev/null 2>&1
sudo systemctl restart arma3
EOF2
echo "arma3-server ($id): players join $ip:2302 with the password in arma3/server.cfg (Direct Connect, or -connect=$ip -port=2302 -password=...)."
echo "Log: ssh -i $key ubuntu@$ip 'tail -f /opt/arma3/profile/*.rpt'"
