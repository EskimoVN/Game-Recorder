#!/usr/bin/env bash
# Creates reforger-server on an EC2 machine (Ubuntu 24.04) and runs the Arma Reforger command-line server with OdxCapture;
# run it again to update the mod and the server settings to this repository's HEAD (uncommitted changes are not sent):
#   bash create-reforger.sh [server build folder]
# Server build folder: the pinned Linux build (data depot + Linux depot merged, document/games/arma-reforger.md), downloaded
# on a PC with DepotDownloader because Steam refuses logins from AWS addresses. Needed on the first run; later runs send it
# only when given (rsync, so only changed files).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(git -C "$here" rev-parse --show-toplevel)
if [ -f "$here/.aws-credentials" ]; then
  export AWS_SHARED_CREDENTIALS_FILE=$here/.aws-credentials AWS_PROFILE=${AWS_PROFILE:-os}
fi
export AWS_REGION=${AWS_REGION:-ap-southeast-1}
build=${1:-}
name=reforger-server
key=$HOME/.ssh/$name.pem
my_ip=$(curl -fsS https://checkip.amazonaws.com)
[ -z "$build" ] || [ -x "$build/ArmaReforgerServer" ] || { echo "$build/ArmaReforgerServer missing or not executable."; exit 1; }

sg=$(aws ec2 describe-security-groups --filters "Name=group-name,Values=$name" --query 'SecurityGroups[0].GroupId' --output text)
if [ "$sg" = None ]; then
  vpc=$(aws ec2 describe-vpcs --filters Name=is-default,Values=true --query 'Vpcs[0].VpcId' --output text)
  sg=$(aws ec2 create-security-group --group-name $name --vpc-id "$vpc" --query GroupId --output text \
    --description "reforger-server: game UDP 2001; SSH from the admin IP")
  aws ec2 authorize-security-group-ingress --group-id "$sg" \
    --ip-permissions 'IpProtocol=udp,FromPort=2001,ToPort=2001,IpRanges=[{CidrIp=0.0.0.0/0}]' > /dev/null
fi
aws ec2 authorize-security-group-ingress --group-id "$sg" --protocol tcp --port 22 --cidr "$my_ip/32" > /dev/null 2>&1 || true

id=$(aws ec2 describe-instances --output text --query 'Reservations[].Instances[].InstanceId' \
  --filters "Name=tag:Name,Values=$name" Name=instance-state-name,Values=pending,running,stopping,stopped)
if [ -z "$id" ]; then
  [ -n "$build" ] || { echo "First run: give the server build folder."; exit 2; }
  if [ ! -f "$key" ]; then
    aws ec2 create-key-pair --key-name $name --key-type ed25519 --query KeyMaterial --output text > "$key"
    chmod 600 "$key"
  fi
  ami=$(aws ssm get-parameter --query Parameter.Value --output text \
    --name /aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/hvm/ebs-gp3/ami-id)
  # The server build is about 10 GB.
  id=$(aws ec2 run-instances --image-id "$ami" --instance-type t3.xlarge --key-name $name --security-group-ids "$sg" \
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
"${ssh_[@]}" 'sudo mkdir -p /opt/reforger/server /opt/reforger/addons /opt/reforger/profile/profile/odx &&
  sudo chown -R ubuntu:ubuntu /opt/reforger'

if [ -n "$build" ]; then
  rsync -a --partial --exclude .DepotDownloader -e "ssh -i $key" "$build/" "ubuntu@$ip:/opt/reforger/server/"
fi
# The addon is replaced as a whole: a stale script left on the server changes its checksum and clients are dropped.
# $profile: is <-profile dir>/profile/, so the mod's $profile:odx/ is /opt/reforger/profile/profile/odx/.
git -C "$repo" archive --format=tar HEAD capture/native/reforger-mod/OdxCapture capture/deploy/aws/reforger | "${ssh_[@]}" '
set -euo pipefail
t=$(mktemp -d) && tar -x -C "$t"
rm -rf /opt/reforger/addons/OdxCapture && mv "$t/capture/native/reforger-mod/OdxCapture" /opt/reforger/addons/
cp "$t"/capture/deploy/aws/reforger/*.txt /opt/reforger/profile/profile/odx/
rm -rf "$t"'

"${ssh_[@]}" 'bash -s' << 'EOF'
set -euo pipefail
dpkg -s libcurl4 net-tools > /dev/null 2>&1 ||
  sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq libcurl4 net-tools > /dev/null
sudo tee /etc/systemd/system/reforger.service > /dev/null << "UNIT"
[Unit]
Description=Arma Reforger command-line server (OdxCapture)
After=network-online.target
Wants=network-online.target

[Service]
User=ubuntu
WorkingDirectory=/opt/reforger/server
ExecStart=/opt/reforger/server/ArmaReforgerServer -server {DFAC5FABD11F2390}Missions/26_CombatOpsEveron.conf -bindPort 2001 -addonsDir /opt/reforger/addons -addons OdxCapture -profile /opt/reforger/profile -maxFPS 60
Restart=on-failure
RestartSec=10
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
UNIT
sudo systemctl daemon-reload
sudo systemctl enable reforger > /dev/null 2>&1
sudo systemctl restart reforger
EOF
echo "reforger-server ($id): players start the game with -client $ip:2001 (the server needs about a minute to load the world)."
