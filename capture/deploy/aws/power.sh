#!/usr/bin/env bash
# Starts, stops or shows one of our EC2 machines, found by its Name tag:
#   bash power.sh <reforger-server|arma3-server|odx-orchestrator> <start|stop|status>
# A stopped machine keeps its disk and Elastic IP; the services on it start again on boot.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
if [ -f "$here/.aws-credentials" ]; then
  export AWS_SHARED_CREDENTIALS_FILE=$here/.aws-credentials AWS_PROFILE=${AWS_PROFILE:-os}
fi
export AWS_REGION=${AWS_REGION:-ap-southeast-1}

[ $# -eq 2 ] || { echo "usage: bash power.sh <reforger-server|arma3-server|odx-orchestrator> <start|stop|status>"; exit 2; }
name=$1 action=$2
id=$(aws ec2 describe-instances --output text --query 'Reservations[].Instances[].InstanceId' \
  --filters "Name=tag:Name,Values=$name" Name=instance-state-name,Values=pending,running,stopping,stopped)
[ -n "$id" ] || { echo "No EC2 machine named $name in $AWS_REGION."; exit 1; }

case $action in
  start)
    aws ec2 start-instances --instance-ids "$id" > /dev/null
    aws ec2 wait instance-running --instance-ids "$id" ;;
  stop)
    aws ec2 stop-instances --instance-ids "$id" > /dev/null
    aws ec2 wait instance-stopped --instance-ids "$id" ;;
  status) ;;
  *) echo "Unknown action $action (start, stop or status)."; exit 2 ;;
esac

read -r state ip type < <(aws ec2 describe-instances --instance-ids "$id" --output text \
  --query 'Reservations[0].Instances[0].[State.Name,PublicIpAddress,InstanceType]')
echo "$name ($id, $type): $state, $ip"
if [ "$state" = running ]; then
  case $name in
    reforger-server) echo "Players: -client $ip:2001 (the server needs about a minute to load the world)" ;;
    arma3-server) echo "Players: Direct Connect $ip:2302 with the password in arma3/server.cfg" ;;
  esac
fi
