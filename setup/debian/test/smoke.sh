#!/usr/bin/env bash
# Install smoke for setup/debian/install.sh in a systemd-booted Debian container.
#
# Usage: smoke.sh <debian-version> <tarball> <tarball-v2>
#   <tarball-v2> is the same build packaged with another -Version (the upgrade step). When its RUNTIME_VERSION
#   differs too, the upgrade stage also checks that the runtime the adopted region still uses is kept.
#   INSTALL_SMOKE_DIR  a published tests/DreamGrid.InstallSmoke (default: published here with dotnet).
#   SMOKE_LOGS         a directory that receives the journal and the launcher's logs when a stage fails.
#   KEEP_CONTAINER=1   leaves the container running afterwards.
#
# install.sh comes from this checkout, as the one-liner downloads it from the release; the unit comes from the
# tarball. Prints one line per stage; exits 0 when every stage passed, 1 at the first failing stage.
set -euo pipefail

if [ $# -ne 3 ]; then
    echo "usage: $0 <debian-version> <tarball> <tarball-v2>" >&2
    exit 2
fi
debian=$1
tarball=$(realpath "$2")
tarball_v2=$(realpath "$3")
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../../.." && pwd)
container=dreamgrid-smoke-$debian
image=dreamgrid-smoke:debian$debian
data=/var/lib/dreamgrid
stage=prepare

ok() { echo "OK   $stage: $*"; }
fail() {
    echo "FAIL $stage: $*"
    exit 1
}
in_container() { docker exec "$container" "$@"; }

on_exit() {
    local code=$?
    if [ "$code" -ne 0 ] && docker inspect "$container" >/dev/null 2>&1; then
        echo "--- journal (dreamgrid) ---"
        in_container journalctl -u dreamgrid --no-pager -n 100 || true
        echo "--- launcher.log ---"
        in_container tail -n 100 "$data/state/logs/launcher.log" || true
        if [ -n "${SMOKE_LOGS:-}" ]; then
            mkdir -p "$SMOKE_LOGS"
            in_container journalctl --no-pager >"$SMOKE_LOGS/journal-debian$debian.txt" 2>&1 || true
            docker cp "$container:$data/state/logs" "$SMOKE_LOGS/logs-debian$debian" >/dev/null 2>&1 || true
        fi
    fi
    if [ "${KEEP_CONTAINER:-0}" != 1 ]; then
        docker rm -f "$container" >/dev/null 2>&1 || true
    fi
    exit "$code"
}
trap on_exit EXIT

version_of() { tar -xzOf "$1" "dreamgrid/$2" | head -n 1; }
v1=$(version_of "$tarball" VERSION)
v2=$(version_of "$tarball_v2" VERSION)
[ "$v1" != "$v2" ] || fail "both tarballs are version $v1"
runtime1=/opt/dreamgrid-runtime/$(version_of "$tarball" RUNTIME_VERSION)
runtime2=/opt/dreamgrid-runtime/$(version_of "$tarball_v2" RUNTIME_VERSION)

work=$(mktemp -d)
if [ -n "${INSTALL_SMOKE_DIR:-}" ]; then
    smoke_dir=$(realpath "$INSTALL_SMOKE_DIR")
else
    smoke_dir=$work/installsmoke
    dotnet publish "$repo/tests/DreamGrid.InstallSmoke" -c Release -o "$smoke_dir" >/dev/null
fi
[ -f "$smoke_dir/DreamGrid.InstallSmoke.dll" ] || fail "no DreamGrid.InstallSmoke.dll in $smoke_dir"

# --- Container ---------------------------------------------------------------------------------------------------
stage=container
docker build -q --build-arg "DEBIAN=$debian" -t "$image" "$here" >/dev/null
docker rm -f "$container" >/dev/null 2>&1 || true
docker run -d --name "$container" --privileged --cgroupns=host -v /sys/fs/cgroup:/sys/fs/cgroup:rw "$image" >/dev/null
# "degraded" (some unit that does not apply to a container failed) is fine; only a boot that never settles is not.
# systemctl fails until systemd's bus is up, so retry the wait.
state=""
for _ in $(seq 1 60); do
    state=$(in_container timeout 120 systemctl is-system-running --wait 2>/dev/null || true)
    case "$state" in running | degraded) break ;; esac
    sleep 1
done
case "$state" in
    running | degraded) ;;
    *) fail "systemd did not boot (state: ${state:-none})" ;;
esac
docker cp "$tarball" "$container:/root/dreamgrid-v1.tar.gz"
docker cp "$tarball_v2" "$container:/root/dreamgrid-v2.tar.gz"
docker cp "$repo/setup/debian/install.sh" "$container:/root/install.sh"
docker cp "$smoke_dir" "$container:/root/installsmoke"
rm -rf "$work"
ok "debian $debian booted with systemd ($state); packages $v1 and $v2"

# --- Busy port (Review Focus 3) ----------------------------------------------------------------------------------
stage=busy-port
docker exec -d "$container" nc -l 8001
for _ in $(seq 1 50); do
    in_container pgrep -x nc >/dev/null && break
    sleep 0.1
done
set +e
out=$(in_container bash /root/install.sh --from /root/dreamgrid-v1.tar.gz 2>&1)
code=$?
set -e
in_container pkill -x nc || true
[ "$code" -eq 1 ] || fail "install.sh exited $code with port 8001 taken, not 1: $out"
sentence="Port 8001 is already used by another program. Re-run with --web-port <free port>, for example --web-port 8101."
grep -qF "$sentence" <<<"$out" || fail "install.sh did not name the taken port: $out"
in_container test ! -e /etc/systemd/system/dreamgrid.service || fail "a unit was written although the port was taken"
ok "install.sh refused with the port sentence"

# --- Install -----------------------------------------------------------------------------------------------------
stage=install
# As the one-liner runs it: the script on stdin, so nothing it starts may read stdin.
set +e
out=$(in_container sh -c 'cat /root/install.sh | bash -s -- --from /root/dreamgrid-v1.tar.gz' 2>&1)
code=$?
set -e
[ "$code" -eq 0 ] || fail "install.sh exited $code: $(tail -n 30 <<<"$out")"
grep -qF "Open this link in your browser to set up your world:" <<<"$out" || fail "no link sentence: $out"
link=$(grep -oE 'http://[^ ]+/setup\?token=[^ ]+' <<<"$out" | tail -n 1)
[ -n "$link" ] || fail "no setup link in the output: $out"
in_container systemctl is-active --quiet dreamgrid || fail "dreamgrid.service is not active"
main_pid=$(in_container systemctl show -p MainPID --value dreamgrid)
user=$(in_container ps -o user= -p "$main_pid" | tr -d ' ')
[ "$user" = dreamgrid ] || fail "the launcher runs as '$user', not dreamgrid"
mode=$(in_container stat -c '%U %a' "$data")
[ "$mode" = "dreamgrid 700" ] || fail "$data is '$mode', not 'dreamgrid 700'"
# The website's styles come from launcher/wwwroot, which only the unit's working directory makes reachable.
for _ in $(seq 1 30); do
    in_container curl -fsS -o /dev/null http://127.0.0.1:8001/css/site.css 2>/dev/null && break
    sleep 1
done
in_container curl -fsS -o /dev/null http://127.0.0.1:8001/css/site.css || fail "the website does not serve /css/site.css"
in_container grep -qF "ExecStart=$runtime1/dotnet " /etc/systemd/system/dreamgrid.service ||
    fail "the unit does not run the launcher on $runtime1"
[ "$(in_container stat -c '%U %a' "$runtime1")" = "root 755" ] || fail "$runtime1 is not root-owned 755"
ok "installed in pipe mode; service active as dreamgrid on $runtime1; link $link"

# --- Setup, logins, service stop/start with adoption (InstallSmoke) -----------------------------------------------
stage=installsmoke
in_container env DOTNET_ROOT="$runtime1" "$runtime1/dotnet" /root/installsmoke/DreamGrid.InstallSmoke.dll \
    --url http://127.0.0.1:8001 --setup-link "$link" --data "$data" --robust-port 8002 \
    --stop "systemctl stop dreamgrid" --start "systemctl start dreamgrid" || fail "InstallSmoke failed"
ok "InstallSmoke passed"

# --- A second launcher on the same data directory (Review Focus 4) -----------------------------------------------
stage=second-launcher
main_pid=$(in_container systemctl show -p MainPID --value dreamgrid)
set +e
out=$(in_container runuser -u dreamgrid -- "$runtime1/dotnet" /opt/dreamgrid/launcher/DreamGrid.Launcher.dll \
    --data "$data" --opensim /opt/dreamgrid/bin --mariadb-bin /usr/sbin 2>&1)
code=$?
set -e
[ "$code" -eq 3 ] || fail "the second launcher exited $code, not 3: $out"
grep -qF "Another DreamGrid launcher is already running with data directory $data." <<<"$out" ||
    fail "the second launcher did not say why: $out"
in_container systemctl is-active --quiet dreamgrid || fail "the service stopped"
[ "$(in_container systemctl show -p MainPID --value dreamgrid)" = "$main_pid" ] || fail "the service's launcher changed"
ok "exit 3 with the lock message; the service's launcher (PID $main_pid) is unaffected"

# --- Upgrade (Review Focus 1) ------------------------------------------------------------------------------------
stage=upgrade
pid_file=$data/state/pids/Smoke_Island.pid
region_pid=$(in_container cat "$pid_file")
settings_before=$(in_container sha256sum "$data/settings.json")
set +e
out=$(in_container bash /root/install.sh --from /root/dreamgrid-v2.tar.gz 2>&1)
code=$?
set -e
[ "$code" -eq 0 ] || fail "install.sh (upgrade) exited $code: $(tail -n 30 <<<"$out")"
grep -qF "Upgraded DreamGrid $v1 → $v2." <<<"$out" || fail "no upgrade line: $out"
footer=""
for _ in $(seq 1 60); do
    footer=$(in_container curl -fsS http://127.0.0.1:8001/login 2>/dev/null | grep -oE '<footer>[^<]*</footer>' || true)
    [ -n "$footer" ] && break
    sleep 2
done
[ "$footer" = "<footer>DreamGrid $v2</footer>" ] || fail "the /login footer is '$footer', not version $v2"
logins=""
for _ in $(seq 1 150); do
    logins=$(in_container curl -fsS http://127.0.0.1:8001/api/status 2>/dev/null | grep -o '"loginsEnabled":true' || true)
    [ -n "$logins" ] && break
    sleep 2
done
[ -n "$logins" ] || fail "logins were not enabled again within 5 minutes"
[ "$(in_container cat "$pid_file")" = "$region_pid" ] || fail "the region PID changed from $region_pid"
in_container kill -0 "$region_pid" || fail "the region (PID $region_pid) is gone"
[ "$(in_container sha256sum "$data/settings.json")" = "$settings_before" ] || fail "settings.json changed"
in_container grep -qF "ExecStart=$runtime2/dotnet " /etc/systemd/system/dreamgrid.service ||
    fail "the unit does not run the launcher on $runtime2"
in_container test -x "$runtime2/dotnet" || fail "$runtime2 is missing"
region_exe=$(in_container readlink "/proc/$region_pid/exe")
if [ "$runtime1" != "$runtime2" ]; then
    [ "$region_exe" = "$runtime1/dotnet" ] || fail "the adopted region runs $region_exe, not $runtime1/dotnet"
    in_container test -x "$runtime1/dotnet" || fail "$runtime1 was removed while the adopted region still uses it"
    runtimes="kept $runtime1 for the adopted region, launcher on $runtime2"
else
    runtimes="runtime $runtime2"
fi
ok "footer shows $v2; logins enabled; region PID $region_pid unchanged; settings.json byte-identical; $runtimes"

stage=finished
echo "Debian $debian install smoke: all stages passed"
