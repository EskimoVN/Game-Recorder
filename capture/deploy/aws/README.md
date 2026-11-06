# EC2 machines (AWS, Singapore)

Credentials: put the access key file in `deploy/aws/.aws-credentials` (ignored by git; its profile `os` is used), or
leave it out and use your own AWS CLI setup (`AWS_PROFILE`). Region: `ap-southeast-1` unless `AWS_REGION` is set.

| Script | Does |
|---|---|
| `bash power.sh <name> start\|stop\|status` | Starts or stops `reforger-server`, `arma3-server` or `odx-orchestrator` and prints its IP. Stopped: only the disk and the Elastic IP are billed. |
| `[ODX_DOMAIN=<domain>] bash create-orchestrator.sh [profile]` | Creates `odx-orchestrator` (t3.medium, Docker, Elastic IP, SSH key `~/.ssh/odx-orchestrator.pem`) and runs `deploy/up.sh` on it. Domain: `ODX_DOMAIN` (its A record must already point at the Elastic IP; the script checks), else `<IP with dashes>.sslip.io` on the first run; later runs keep the machine's domain unless `ODX_DOMAIN` is given. Run it again to deploy the repository's HEAD; `.env` (access key), `state/` and `zerotier/` are kept. `profile` sets `ODX_GAME_PROFILE`, e.g. `arma-reforger.json`. |

It prints the orchestrator's address and access key. Put them in the repository's GitHub settings (variable
`ODX_ORCHESTRATOR` = `https://<domain>`, secret `ODX_ACCESS_KEY`); the workflow "Release capture package" then builds the
players' package for it (tag `capture-v<version>` or Run workflow) and attaches the zip to a release. With a domain of our
own the package stays valid when the machine is replaced.

`bash create-reforger.sh [server build folder]` creates `reforger-server` (t3.xlarge, 60 GB, Elastic IP, SSH key
`~/.ssh/reforger-server.pem`, UDP 2001 open): the Arma Reforger 1.6.0.68 command-line server, `-client <IP>:2001`. The
build (data depot + Linux depot, manifests in `document/games/arma-reforger.md`) is downloaded on a PC with DepotDownloader,
because Steam refuses logins from AWS addresses, and uploaded by the script; the first run needs it. Each run replaces the
`OdxCapture` mod (`/opt/reforger/addons/`, lowercase `scripts/`) and `reforger/*.txt` (`$profile:odx/`) with HEAD's and
restarts the systemd unit `reforger`.

`bash create-arma3.sh [server build folder]` creates `arma3-server` (t3.xlarge, 60 GB, Elastic IP, SSH key
`~/.ssh/arma3-server.pem`, UDP 2302–2305 open): the Arma 3 dedicated server 2.18.152405 with BattlEye off
(`arma3/server.cfg`), the Custom difficulty with the HUD off (`arma3/odx.Arma3Profile`) and the test mission
`arma3/mission/odx_test.stratis` (16 playable riflemen on Stratis, respawn at `respawn_west`), packed on the machine by
`arma3/pack_pbo.py`. The build (content depot 233781 + Linux depot 233783 of app 233780, manifests in
`document/games/arma3.md`) is downloaded on a PC with DepotDownloader and a Steam account (anonymous login is refused for this
app; it need not own the game), because Steam refuses logins from AWS addresses, and uploaded by the script; the first run needs
it. Each run replaces the config, profile and mission with HEAD's and restarts the systemd unit `arma3`. Players join by
Direct Connect `<IP>:2302` with the password, on the same game version.
