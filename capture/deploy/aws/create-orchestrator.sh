#!/usr/bin/env bash
# Creates odx-orchestrator on an EC2 machine (Ubuntu 24.04 + Docker) and deploys capture/deploy on it with up.sh; run it
# again to update the machine to this repository's HEAD (uncommitted changes are not sent):
#   [ODX_DOMAIN=capture.example.com] bash create-orchestrator.sh [game profile, a file of capture/profiles/]
# ODX_DOMAIN: our own name for the orchestrator, baked into the players' package, so the package never changes when the
# machine does; its DNS A record must point at the Elastic IP (DNS only, no proxy: players reach <domain>:12300/udp).
# Without it the first run uses <Elastic IP>.sslip.io, a public DNS name that resolves to the IP inside it; a later run
# keeps whichever domain the machine already has.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(git -C "$here" rev-parse --show-toplevel)
if [ -f "$here/.aws-credentials" ]; then
  export AWS_SHARED_CREDENTIALS_FILE=$here/.aws-credentials AWS_PROFILE=${AWS_PROFILE:-os}
fi
export AWS_REGION=${AWS_REGION:-ap-southeast-1}
profile=${1:-}
name=odx-orchestrator
key=$HOME/.ssh/$name.pem
my_ip=$(curl -fsS https://checkip.amazonaws.com)

sg=$(aws ec2 describe-security-groups --filters "Name=group-name,Values=$name" --query 'SecurityGroups[0].GroupId' --output text)
if [ "$sg" = None ]; then
  vpc=$(aws ec2 describe-vpcs --filters Name=is-default,Values=true --query 'Vpcs[0].VpcId' --output text)
  sg=$(aws ec2 create-security-group --group-name $name --vpc-id "$vpc" --query GroupId --output text \
    --description "odx-orchestrator: HTTPS, time service, ZeroTier; SSH from the admin IP")
  # Same ports as capture/README.md "Deploy the orchestrator"; 9993/tcp (the ZeroTier controller's API) stays closed.
  aws ec2 authorize-security-group-ingress --group-id "$sg" --ip-permissions \
    'IpProtocol=tcp,FromPort=80,ToPort=80,IpRanges=[{CidrIp=0.0.0.0/0}]' \
    'IpProtocol=tcp,FromPort=443,ToPort=443,IpRanges=[{CidrIp=0.0.0.0/0}]' \
    'IpProtocol=udp,FromPort=443,ToPort=443,IpRanges=[{CidrIp=0.0.0.0/0}]' \
    'IpProtocol=udp,FromPort=12300,ToPort=12300,IpRanges=[{CidrIp=0.0.0.0/0}]' \
    'IpProtocol=udp,FromPort=9993,ToPort=9993,IpRanges=[{CidrIp=0.0.0.0/0}]' > /dev/null
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
  # t3.medium: the image is built on the machine and dotnet publish needs more than 2 GB.
  id=$(aws ec2 run-instances --image-id "$ami" --instance-type t3.medium --key-name $name --security-group-ids "$sg" \
    --block-device-mappings 'DeviceName=/dev/sda1,Ebs={VolumeSize=30,VolumeType=gp3}' \
    --metadata-options HttpTokens=required \
    --user-data $'#!/bin/bash\ncurl -fsSL https://get.docker.com | sh\nusermod -aG docker ubuntu\ntouch /var/lib/cloud/odx-ready' \
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
domain=${ODX_DOMAIN:-}
if [ -n "$domain" ] && [ "$(dig +short "$domain" A | tail -1)" != "$ip" ]; then
  echo "Point the DNS A record of $domain at $ip (DNS only), wait until it resolves, then run this again."
  exit 1
fi
ssh_=(ssh -i "$key" -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 "ubuntu@$ip")
echo "Waiting for $ip (Docker is installed on the first boot)..."
until "${ssh_[@]}" test -f /var/lib/cloud/odx-ready 2> /dev/null; do sleep 10; done

# Extracted over the previous copy, so deploy/.env, deploy/state/ and deploy/zerotier/ survive an update.
git -C "$repo" archive --format=tar HEAD capture | "${ssh_[@]}" 'mkdir -p ~/odx && tar -x -C ~/odx'
# ssh joins its arguments into one command line, so each is quoted (an empty domain must stay an argument).
"${ssh_[@]}" "bash -s -- $(printf '%q ' "$domain" "${ip//./-}.sslip.io" "$profile")" << 'EOF'
set -euo pipefail
cd ~/odx/capture/deploy
[ -f .env ] || bash up.sh > /dev/null || true
if [ -n "$1" ]; then
  sed -i "s/^ODX_DOMAIN=.*/ODX_DOMAIN=$1/" .env
elif grep -q '^ODX_DOMAIN=capture.example.com$' .env; then
  sed -i "s/^ODX_DOMAIN=.*/ODX_DOMAIN=$2/" .env
fi
[ -z "$3" ] || sed -i "s/^ODX_GAME_PROFILE=.*/ODX_GAME_PROFILE=$3/" .env
bash up.sh
# The orchestrator's SNTP responder answers from this clock (spec v3 §4 layer 1): it must be NTP-synchronized.
sudo timedatectl set-ntp true
echo "Host clock NTP-synchronized: $(timedatectl show -p NTPSynchronized --value)"
EOF
